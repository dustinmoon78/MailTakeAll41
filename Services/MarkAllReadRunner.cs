// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChatShared;
using Comfort.Common;
using EFT;
using EFT.UI.Chat;
using MailTakeAll41.Ui;
using UnityEngine;

namespace MailTakeAll41.Services
{
	/// <summary>
	/// 「全部已读」编排器：把所有带未读/未领标记的会话一次性标为已读。
	///
	/// ★语义（用户拍板）：只标已读，不删任何会话、不删任何消息。
	///   效果 = 左侧会话条目的「未读消息」与「未领附件」两个角标都清掉 + 通知服务端标记已读。
	///
	/// ★五个必须避开的坑（均在代码里有对应规避）：
	///   1. 分页：social.Dialogues 只含已加载的会话，每页 30 条、玩家不滚列表就只加载 30 条。
	///      不补拉 ⇒ 未加载的会话既不清角标、也不进 /client/mail/dialog/read 的 id 数组，
	///      服务端未读状态保留、重登复现。故本流程先跑 DialogueLoader.LoadAllAsync 补拉。
	///      ★补拉**不能**用原版 SocialNetwork.UpdateDialogueList：它每页会把第一个会话自动选中
	///      ⇒ MenuTaskBar.DialogueSelectedHandler 把该会话的 New / AttachmentsNew 清零
	///      ⇒ 那个会话反而落选、且玩家当前会话被切走。DialogueLoader 已改为直取服务端载荷。
	///   2. 共用队列：原版 ReadDialogues 在**回执回调里**把 ReadDialoguesQueue 整个 Clear()。
	///      我们若把会话塞进那个共享队列，回执期间原版新入队的条目会被连带抹掉、
	///      它的已读请求永远发不出去。故本流程**不碰**那个队列，直接调 _session.ChatReadDialogues。
	///   3. 回执判失败：Backend.SendVoid 成功失败都会回调（Backend.cs:241-256），
	///      不看 IResult.Succeed 会把服务端失败报成「回执已收到」。故按 Succeed 判定。
	///   4. 角标刷新：走 OnLastMessageUpdated 事件会连带触发 MessagesContainer 的 ScrollToTheBottom
	///      （把玩家翻历史拽回底部），且该事件一次只刷一个角标。
	///      故改用 DialogueBadgeRefresher 直接刷可见格子的两个角标。
	///   5. 清 AttachmentsNew 会打瞎「全部收取」：AttachmentsNew > 0 是「从没点开过的会话」
	///      唯一可靠的未领附件信号。清零之前先把这些会话登记进 PendingAttachments，
	///      「全部收取」的候选集再加上这一条 ⇒ 两个按钮的先后顺序不再影响收取结果。
	/// </summary>
	internal static class MarkAllReadRunner
	{
		/// <summary>等服务端回执的上限（毫秒）。回执只说明请求走完，超时不影响本地效果。</summary>
		private const int AckTimeoutMs = 8000;

		internal static void Run(SocialNetwork social)
		{
			// 与「全部收取」共用一个闸：两个按钮并排，同时点会互相踩状态。
			if (!OperationGate.TryEnter("全部已读"))
			{
				return;
			}

			MarkAllReadButton.SetBusy(true);

			// ★fire-and-forget：绝不 await。内部要等服务端回执，
			//   在任何 Harmony 补丁或 UnityEvent 回调里 await 都会出问题（见 TakeAllRunner 坑 3）。
			Task task = RunAsync(social);
			if (task == null)
			{
				OperationGate.Exit();
				MarkAllReadButton.SetBusy(false);
				MailTakeAll41Plugin.Log?.LogError("[MailTakeAll] 启动「全部已读」流程失败：返回了 null Task");
				return;
			}

			task.ContinueWith(
				t =>
				{
					AggregateException ex = t.Exception;
					if (ex != null)
					{
						// 未观察到的 task 异常会被 Unity 静默吞掉，这里显兜住，保证永不静默
						MailTakeAll41Plugin.Log?.LogError("[MailTakeAll] 「全部已读」流程异常：" + ex);
					}
				},
				TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
		}

		private static async Task RunAsync(SocialNetwork social)
		{
			try
			{
				if (social == null)
				{
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] SocialNetwork 为空，无法标记已读");
					return;
				}

				// —— 坑 1：先补拉全量会话（直取服务端载荷，不走原版那条带自动选中副作用的路径）——
				DialogueLoadResult load = await DialogueLoader.LoadAllAsync(social).ConfigureAwait(true);

				// 待处理集合：本地有未读标记，**或** 服务端载荷直接报告有未读。
				// ★为什么两个都要：
				//   本地 New / AttachmentsNew 会被原版的自动选中清零（见坑 1），
				//   而服务端载荷的 @new / attachmentsNew 是权威值，不受本地清零影响。
				//   只看本地 ⇒ 被清零过的会话会漏标；只看服务端 ⇒ 服务端不推送时看不到本地实时状态。
				var targets = new List<UpdatableChatDialogue>();
				var seen = new HashSet<string>(StringComparer.Ordinal);

				foreach (UpdatableChatDialogue dialogue in social.Dialogues)
				{
					if (dialogue == null || string.IsNullOrEmpty(dialogue._id))
					{
						continue;
					}

					// ★排除 GlobalChat：原版 AddDialogueToReadQueue 同样排除它
					//   （SocialNetwork.cs:1439），全局频道不是个人消息，标了也没意义。
					if (dialogue.Type == EMessageType.GlobalChat)
					{
						continue;
					}

					if (dialogue.New > 0 || dialogue.AttachmentsNew > 0)
					{
						targets.Add(dialogue);
						seen.Add(dialogue._id);
					}
				}

				// 服务端权威补齐：本地计数被清零、但服务端仍说有未读的会话。
				// 这些会话在本地可能已不在 Dialogues 里（未加载），但服务端知道它的 id。
				// ★必须排除全局频道：原版 AddDialogueToReadQueue 明确排除它
				//   （SocialNetwork.cs:1439 Type != EMessageType.GlobalChat），全局频道不是个人消息。
				List<string> serverOnly = load.ServerReportedUnreadIds
					.Where(id => !string.IsNullOrEmpty(id)
						&& !seen.Contains(id)
						&& !load.GlobalChatIds.Contains(id))
					.ToList();

				// 坑 5 的连带处置：清角标前先记住「有未领附件」的会话。
				// 服务端载荷与本地计数两边都算，覆盖「本地被清零、服务端仍报有附件」的情况。
				int pendingMarked = 0;
				foreach (UpdatableChatDialogue dialogue in targets)
				{
					// ★本地计数 > 0 **或** 服务端载荷报了 attachmentsNew>0，两边都算。
					//   玩家打开过的会话本地计数已被清零（DialogueView.SelectView），
					//   但服务端可能仍报有附件 ⇒ 只看本地会两头落空、漏登记。
					if (dialogue.AttachmentsNew > 0 || load.ServerReportedAttachmentIds.Contains(dialogue._id))
					{
						PendingAttachments.Add(dialogue._id);
						pendingMarked++;
					}
				}

				foreach (string id in serverOnly)
				{
					// 服务端说这个会话有未读，但本地没加载出来 ⇒ 无法清本地角标，
					//   但**必须**把 id 发给服务端，否则重登后未读复现。
					// ★PendingAttachments 的语义是「仍有未领附件」，不是「有未读」。
					//   只有服务端明确报了 attachmentsNew>0 的会话才登记进去，
					//   否则纯未读消息的会话会被塞进「全部收取」候选集、白打一次请求，
					//   且 id 会永久留在落盘文件里。
					if (load.ServerReportedAttachmentIds.Contains(id))
					{
						PendingAttachments.Add(id);
					}
				}

				if (targets.Count == 0 && serverOnly.Count == 0)
				{
					MailTakeAll41Plugin.Log?.LogInfo("[MailTakeAll] 没有未读会话，「全部已读」无事可做");
					return;
				}

				// —— 服务端标记已读 ——
				// ★坑 2：不复用 social.ReadDialogues / social.ReadDialoguesQueue。
				//   原版那个方法在回执回调里 Clear 整个共享队列，会把回执期间原版新入队的条目抹掉。
				//   这里直接调 ISocial.ChatReadDialogues（同一个 POST {messaging}/client/mail/dialog/read），
				//   与原版共享队列完全隔离。
				//
				// ★id 集合 = 本地有未读的会话 + 服务端报告有未读的会话，两边并集。
				var ids = new List<string>();
				foreach (UpdatableChatDialogue dialogue in targets)
				{
					ids.Add(dialogue._id);
				}

				ids.AddRange(serverOnly);
				ids = ids.Distinct(StringComparer.Ordinal).ToList();

				var tcs = new TaskCompletionSource<bool>();
				bool sent = false;

				try
				{
					ISocial session = social._session;
					if (session == null)
					{
						MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 会话后端为空，无法通知服务端标记已读（本地角标仍会清）");
					}
					else
					{
						// ★坑 3：按 IResult.Succeed 判定成败。
						//   Backend.SendVoid 成功失败都会 Invoke 回调（Backend.cs:241-256），
						//   不看 Succeed 会把服务端失败报成「回执已收到」。
						session.ChatReadDialogues(ids.ToArray(), delegate(IResult r)
						{
							tcs.TrySetResult(r != null && r.Succeed);
						});
						sent = true;
					}
				}
				catch (Exception ex)
				{
					MailTakeAll41Plugin.Log?.LogError("[MailTakeAll] 发送已读标记失败：" + ex);
				}

				// —— 本地立即清零 + 刷角标：不等服务端，玩家点了要立刻看到效果 ——
				int cleared = ClearLocalUnreadFlags(targets);

				// —— 等回执（最多 8 秒）——
				bool acked = sent && await WithTimeout(tcs.Task, AckTimeoutMs).ConfigureAwait(true);

				MailTakeAll41Plugin.Log?.LogInfo(
					"[MailTakeAll] 全部已读完成：本地待处理会话 " + targets.Count +
					"，服务端补充会话 " + serverOnly.Count +
					"，未领附件会话已登记 " + pendingMarked +
					"，本地已清零 " + cleared +
					"，提交已读 id " + ids.Count +
					"，服务端回执 " + (acked ? "成功" : "失败或超时") +
					(load.PagedToEnd ? "" : "，注意：会话列表未补拉到底，可能有会话漏标"));
			}
			catch (Exception ex)
			{
				MailTakeAll41Plugin.Log?.LogError("[MailTakeAll] 「全部已读」流程失败：" + ex);
			}
			finally
			{
				OperationGate.Exit();
				MarkAllReadButton.SetBusy(false);
			}
		}

		/// <summary>
		/// 本地清零未读/未领计数，并刷新列表里可见格子的角标。
		/// 返回真正清掉的会话数。
		/// </summary>
		private static int ClearLocalUnreadFlags(List<UpdatableChatDialogue> targets)
		{
			int cleared = 0;

			foreach (UpdatableChatDialogue dialogue in targets)
			{
				try
				{
					dialogue.New = 0;
					dialogue.AttachmentsNew = 0;
					cleared++;
				}
				catch (Exception ex)
				{
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 清零会话 " + Short(dialogue._id) + " 的未读标记时异常：" + ex.Message);
				}
			}

			// 刷可见格子的两个角标（不触发事件，避免 MessagesContainer 把玩家翻历史拽回底部）
			//
			// ★搜索根要够大：格子实际挂在 LightScroller 的 _targetArea 下
			//   （LightScroller.cs:414 / 732 / 738），而 _targetArea 是否是 _dialoguesContainer 的后代
			//   取决于 UI prefab 装配，反编译源码里没有 prefab、无法静态证明。
			//   ⇒ 这里按「会话列表容器 → ChatScreen → 全部」多根兜底，任一根命中即可。
			try
			{
				var chatScreen = MonoBehaviourSingleton<EFT.UI.CommonUI>.Instance?.ChatScreen;
				var roots = new List<Transform>();

				if (chatScreen != null)
				{
					if (chatScreen._dialoguesContainer != null)
					{
						roots.Add(chatScreen._dialoguesContainer.transform);
					}

					roots.Add(chatScreen.transform);
				}

				int refreshed = DialogueBadgeRefresher.Refresh(roots, targets);
				if (refreshed == 0 && targets.Count > 0)
				{
					// 一个格子都没命中：角标不会立刻消失（要等该会话滚出再滚回才恢复正确）。
					// 这条必须可见，便于实机对账「是不是搜索根不对」。
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 未找到任何可见会话格子，角标可能不会立即消失（待处理会话 " + targets.Count + " 个）");
				}
				else
				{
					MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll] 已刷新 " + refreshed + " 个可见会话格子的角标");
				}
			}
			catch (Exception ex)
			{
				MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 刷新会话角标失败（计数已清零，只是显示可能滞后）：" + ex.Message);
			}

			// 主菜单/顶栏的未读总数是另算的（MenuTaskBar.SetNewMessagesCount 逐会话求和），要单独刷一次
			// ★先判 Instantiated 再取 Instance：Singleton<T>.Instance 在未创建时的行为由 Comfort 决定，
			//   原版 SocialNetwork.CG_method_12 也是先判 Instantiated 再取实例，照抄这个口径。
			try
			{
				if (MonoBehaviourSingleton<EFT.UI.PreloaderUI>.Instantiated)
				{
					EFT.UI.MenuTaskBar taskBar = MonoBehaviourSingleton<EFT.UI.PreloaderUI>.Instance?.MenuTaskBar;
					taskBar?.SetNewMessagesCount();
				}
			}
			catch (Exception ex)
			{
				MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll] 刷新主菜单未读总数失败（不影响功能）：" + ex.Message);
			}

			return cleared;
		}

		/// <summary>把一个永不完成的 task 变成可等待的：超时返回 false，正常完成返回 task 的结果。</summary>
		private static async Task<bool> WithTimeout(Task<bool> task, int timeoutMs)
		{
			Task finished = await Task.WhenAny(task, Task.Delay(timeoutMs)).ConfigureAwait(true);
			if (ReferenceEquals(finished, task))
			{
				return await task.ConfigureAwait(true);
			}

			return false;
		}

		private static string Short(string id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return "(空)";
			}

			return id.Length <= 8 ? id : id.Substring(0, 8);
		}
	}
}
