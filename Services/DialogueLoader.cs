// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChatShared;
using Comfort.Common;
using EFT;

namespace MailTakeAll41.Services
{
	/// <summary>一轮补拉的结果。</summary>
	internal sealed class DialogueLoadResult
	{
		/// <summary>true = 已补到底（服务端返回条数 &lt; 30）。</summary>
		internal bool PagedToEnd;

		/// <summary>★补拉结束后本地会话总数（含本轮之前就有的），仅用于日志对账。</summary>
		internal int LoadedCount;

		/// <summary>服务端载荷直接报告「有未读消息或未领附件」的会话 id（权威值）。</summary>
		internal List<string> ServerReportedUnreadIds = new List<string>();

		/// <summary>服务端载荷直接报告「有未领附件」的会话 id（权威值）。</summary>
		internal List<string> ServerReportedAttachmentIds = new List<string>();

		/// <summary>★全局频道（GlobalChat）的会话 id：原版一律不把它们纳入已读标记。</summary>
		internal HashSet<string> GlobalChatIds = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>true = 本轮已超时作废，迟到的回调不再采信。</summary>
		internal bool Cancelled;
	}

	/// <summary>
	/// 会话列表补拉分页（两个批量流程共用）。
	///
	/// ★为什么不用原版 SocialNetwork.UpdateDialogueList（这是本模块最重要的一条）：
	///   原版每页回调里执行 CreateDialogue(array[num2], num2 == 0) —— 第二个参数就是 select=true
	///   ⇒ **每页第一个会话被自动选中**。SetDialogueSelected 随即触发 OnDialogueSelectedGlobal
	///   ⇒ MenuTaskBar.DialogueSelectedHandler 把该会话的 New / AttachmentsNew 写死为 0
	///   （SocialNetwork.cs:483 / 1466-1479；MenuTaskBar.cs:769-777）。
	///   若复用它补拉，被自动选中的那个会话会因为计数已被清零而落选，
	///   它的 id 不进 /client/mail/dialog/read ⇒ 服务端未读保留、重登复现；
	///   同时玩家当前正在看的会话会被切走。
	///   ⇒ 本模块**直接调 _session.ChatGetDialogList 自取载荷**，不走原版那条带副作用的路径。
	///
	/// ★游标必须从 0 开始、按「服务端实际返回条数」推进（这是第二轮审查后的关键修正）：
	///   原版用 Dialogues.Count() 当 offset，等于「跳过本地已加载的那部分」。
	///   若我们也这么干，服务端**前 N 页**（本地已有的那批）永远不会被查询，
	///   也就采不到它们的 @new / attachmentsNew。
	///   而恰恰是本地已有的这批最可能本地计数为 0：
	///     · 登录时 CreateDialogue(array[0], select:true) 自动选中首个会话
	///       ⇒ MenuTaskBar.DialogueSelectedHandler 把它 New/AttachmentsNew 写死 0；
	///     · 玩家点开会话时 DialogueView.SelectView(true) → NewMessagesCount = 0 → 写回 _dialogue.New = 0。
	///   ⇒ 这批会话既不在「本地有未读」集合里，服务端状态又没被采到
	///   ⇒ id 不进 /client/mail/dialog/read ⇒ 服务端未读保留 ⇒ **重登后未读复现**。
	///   改成「offset=0 起、按服务端返回条数推进」后，服务端列表被完整遍历一遍，
	///   每间会话的权威 @new / attachmentsNew 都能采到。
	///   本地是否已有交给 CreateDialogue(room, false) 自己判断（已存在则直接返回、不刷新计数）。
	///
	/// ★ChatRoomInformation 载荷自带服务端的 @new（未读消息数）与 attachmentsNew（未领附件数）
	///   （ChatShared/ChatRoomInformation.cs:16-18）——这是服务端视角的权威值，
	///   不受本地谁被自动选中、谁被清零的影响。
	///
	/// ★与原版 UpdateDialogueList 互斥：
	///   原版 DialoguesContainer.Update() 每帧在 !FullyRead && !MessageListUpdating 时会调
	///   UpdateDialogueList，那条路径会再自动选中一次并清零（DialoguesContainer.cs:169-183）。
	///   插件补拉期间置 MessageListUpdating = true，结束恢复原值，避免两边同时拉。
	///
	/// ★残留（原版在途请求，插件无法取消）：
	///   MessageListUpdating 只挡住原版**发起新请求**，挡不住**已经在飞**的那个请求 ——
	///   它的回调仍会 CreateDialogue(array[0], select:true) ⇒ 自动选中第一个会话
	///   ⇒ 把玩家当前会话切走、并清零那个会话的计数。
	///   **插件刻意不恢复选中状态**：恢复需要在补拉窗口内区分「被原版顶掉」与
	///   「玩家自己点了别的会话」，而两者在 SocialNetwork 上无法区分；强行恢复会把玩家的
	///   手动选择也顶掉、并顺带清掉回复引用，副作用比不恢复更大。
	///   影响面可控：被顶掉的那个会话虽然本地计数被清零，但它仍会经「服务端权威值」
	///   进入已读提交集合（覆盖面不丢）。
	///
	/// ★终止条件：服务端返回条数 &lt; 30 即到底（与原版 856-859 行同口径）。
	///   不能拿 MessageListUpdating 当守卫：原版在「取到数据」分支里直接 return，
	///   跳过了把它置回 false 的那行（SocialNetwork.cs:867-868 vs 877），
	///   所以该标志在补拉成功后**恒为 true**、不能当守卫。
	/// </summary>
	internal static class DialogueLoader
	{
		/// <summary>补拉轮次硬上限。理论每轮 +30 条，64 轮 = 1920 个会话，远超实际需要，纯属防跑飞兜底。</summary>
		private const int MaxPagingRounds = 64;

		/// <summary>单轮补拉等待回调的上限（毫秒），网络卡死时不让整轮流程永远挂着。</summary>
		private const int PagingRoundTimeoutMs = 15000;

		/// <summary>与服务端一致的每页条数（原版硬编码 30）。</summary>
		private const int PageSize = 30;

		/// <summary>
		/// 循环调 ChatGetDialogList 把服务端会话列表完整遍历一遍。
		/// </summary>
		internal static async Task<DialogueLoadResult> LoadAllAsync(SocialNetwork social)
		{
			var result = new DialogueLoadResult();
			if (social == null)
			{
				return result;
			}

			ISocial session = social._session;
			if (session == null)
			{
				MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 会话后端为空，无法补拉会话列表");
				return result;
			}

			// 与原版 UpdateDialogueList 互斥：补拉期间不让原版同时拉（它会自动选中并清零计数）
			bool originalUpdating = social.MessageListUpdating;
			social.MessageListUpdating = true;

			try
			{
				await LoadAllCoreAsync(social, session, result).ConfigureAwait(true);
			}
			finally
			{
				// 恢复原值（不学原版那个「return 跳过置回 false」的写法）
				social.MessageListUpdating = originalUpdating;
			}

			return result;
		}

		private static async Task LoadAllCoreAsync(SocialNetwork social, ISocial session, DialogueLoadResult result)
		{
			int round = 0;

			// ★游标从 0 开始：服务端列表被完整遍历，每间会话的权威 @new / attachmentsNew 都能采到。
			//   游标按「服务端实际返回条数」推进，绝不用 Dialogues.Count()
			//   （CreateDialogue 对已存在会话不新增，用它会原地打转）。
			int serverOffset = 0;

			while (round < MaxPagingRounds)
			{
				round++;

				// ★刻意不因为 social.FullyRead 就提前收工：
				//   FullyRead 只说明「原版自己的翻页结束了」，不代表我们已经采到全部会话的
				//   服务端权威 @new。只有「我们自己拉到不足一页」才算到底。

				int offset = serverOffset;
				var tcs = new TaskCompletionSource<bool>();

				try
				{
					session.ChatGetDialogList(PageSize, offset, delegate(Result<ChatRoomInformation[]> r)
					{
						try
						{
							// ★超时作废：迟到的回调不再建会话、不再采 @new。
							//   否则目标集合已算完，这页会话会「既没清角标也没上报已读」，
							//   而日志只留一句「可能有会话漏收」。
							if (result.Cancelled)
							{
								tcs.TrySetResult(false);
								return;
							}

							// ★失败与空结果必须分开：
							//   原版 UpdateDialogueList 在请求失败时也走同一个 callback?.Invoke()，
							//   失败被当成「本轮无新增」⇒ 提前停止补拉并静默半途而废。
							//   Result<T> 是 struct，不能与 null 比较，只能看 Succeed / Error。
							if (!r.Succeed)
							{
								string err = string.IsNullOrEmpty(r.Error) ? "(无错误说明)" : r.Error;
								MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 补拉对话列表失败：" + err);
								tcs.TrySetResult(false);
								return;
							}

							ChatRoomInformation[] rooms = r.Value;
							int num = rooms == null ? 0 : rooms.Length;

							if (num < PageSize)
							{
								social.FullyRead = true;
								result.PagedToEnd = true;
							}

							if (num > 0)
							{
								foreach (ChatRoomInformation room in rooms)
								{
									if (room == null || string.IsNullOrEmpty(room.Id))
									{
										continue;
									}

									// ★全局频道不纳入已读标记：原版 AddDialogueToReadQueue 明确排除它
									//   （SocialNetwork.cs:1439 Type != EMessageType.GlobalChat）。
									//   ChatRoomInformation.type 就是 EMessageType 的数值。
									if (room.type == (int)EMessageType.GlobalChat)
									{
										result.GlobalChatIds.Add(room.Id);
									}

									// 服务端视角的未读信息（权威，不受本地清零影响）
									if (room.@new > 0 || room.attachmentsNew > 0)
									{
										result.ServerReportedUnreadIds.Add(room.Id);
									}

									if (room.attachmentsNew > 0)
									{
										result.ServerReportedAttachmentIds.Add(room.Id);
									}

									// ★select: false —— 关键：不触发自动选中、不清任何会话的计数。
									//   CreateDialogue 对已存在的会话直接返回、不刷新计数（SocialNetwork.cs:818-831），
									//   这与原版行为一致，本插件不改动既有会话的计数语义。
									try
									{
										social.CreateDialogue(room, false);
									}
									catch (Exception ex)
									{
										MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 建会话 " + Short(room.Id) + " 时异常：" + ex.Message);
									}
								}
							}

							// 游标按服务端实际返回条数推进
							serverOffset += num;

							tcs.TrySetResult(true);
						}
						catch (Exception ex)
						{
							MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 处理对话列表回调时异常：" + ex.Message);
							tcs.TrySetResult(false);
						}
					});
				}
				catch (Exception ex)
				{
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 请求对话列表失败：" + ex.Message);
					return;
				}

				bool ok = await WithTimeout(tcs.Task, PagingRoundTimeoutMs).ConfigureAwait(true);
				if (!ok)
				{
					// 请求失败或超时：明确报「可能漏收」，不能当成「到底了」静默收工。
					result.Cancelled = true;
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 补拉第 " + round + " 轮失败或超时，停止补拉（可能有会话漏收）");
					return;
				}

				result.LoadedCount = social.Dialogues.Count();

				MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll] 补拉第 " + round + " 轮，服务端偏移 " + serverOffset + "，本地会话数 " + result.LoadedCount);

				if (result.PagedToEnd)
				{
					break;
				}
			}

			if (round >= MaxPagingRounds && !result.PagedToEnd)
			{
				MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 补拉达到轮次上限 " + MaxPagingRounds + "，已停止（可能有会话漏收）");
			}
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
