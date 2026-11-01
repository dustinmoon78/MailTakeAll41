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
	/// <summary>一轮收集的统计结果，用于可见日志与排查。</summary>
	internal sealed class CollectResult
	{
		internal int DialogTotal;
		internal int CandidateCount;

		/// <summary>候选里靠 AttachmentsNew&gt;0（列表载荷自带的未领附件计数）命中的会话数，仅供实机对账。</summary>
		internal int AttachmentFlagCount;
		internal int FetchedDialogs;
		internal int FailedDialogs;
		internal int RawMessageCount;
		internal int DuplicateRemoved;

		/// <summary>★true = 会话列表**没**补拉到底，可能有会话漏收（命名即语义，避免极性反着用）。</summary>
		internal bool PagingIncomplete;

		internal bool FetchTimedOut;
		internal List<DialogueChatMessage> Merged;

		/// <summary>
		/// 本轮**确认成功返回且确实没有附件**的会话 id（用于清理 PendingAttachments 登记）。
		/// ★必须是数组快照：超时后的迟到回调仍会往活动 List 里写，
		///   而调用方拿到本结果后要无锁枚举它 ⇒ 活动 List 会撞上并发修改、整轮作废。
		/// </summary>
		internal string[] SettledDialogIds = new string[0];
	}

	/// <summary>
	/// 附件收集器：补拉全量会话 → 逐会话取附件 → 按消息 id 去重合并。
	///
	/// ★为什么必须补拉：
	///   SocialNetwork.Dialogues 只含**已加载**的会话，而列表每页只有 30 条。
	///   玩家没滚过左侧列表时，未加载的会话根本不在 Dialogues 里，直接遍历会静默漏收。
	///   补拉逻辑见 DialogueLoader（「全部收取」「全部已读」共用）。
	///
	/// ★为什么必须去重：
	///   同一批附件消息可能出现在多个会话的返回里，按 DialogueChatMessage._id 去重后才敢一次性喂给 ProcessRewards。
	///
	/// ★为什么不用 SocialNetwork.AllAttachmentsFromDialog（这是本模块最重要的一条）：
	///   原版那个包装在「请求失败」与「确实没有附件」两种情况下**都回调 null**
	///   （SocialNetwork.cs:1509-1512 与 1520-1523），调用方区分不了；
	///   而 getAllAttachments 是 NoRetries（ClientBackendSession.cs:2468-2476），一次网络抖动就触发失败分支。
	///   本插件要靠「确实没有附件」这个结论去清理 PendingAttachments 登记，
	///   误把失败当空 ⇒ 把仍有附件的会话登记删掉 ⇒ 可能永久漏收（比「只增不减」更糟）。
	///   ⇒ 这里直接调 ISocial.AllAttachmentsFromDialog，拿得到 Result&lt;ChatMessagesList&gt;，
	///   只有 Succeed=true 且消息为空时才算「确实没有附件」。
	/// </summary>
	internal static class DialogueCollector
	{
		/// <summary>取附件阶段整体等待上限（毫秒）。</summary>
		private const int FetchTimeoutMs = 30000;

		internal static async Task<CollectResult> CollectAsync(SocialNetwork social)
		{
			var result = new CollectResult { Merged = new List<DialogueChatMessage>() };
			if (social == null)
			{
				return result;
			}

			// 补拉全量会话（共用 DialogueLoader：「全部已读」也走同一条分页路径）
			DialogueLoadResult load = await DialogueLoader.LoadAllAsync(social).ConfigureAwait(true);
			result.PagingIncomplete = !load.PagedToEnd;

			// ★补拉到底后，顺手清掉「已被玩家删除的会话」的待收登记：
			//   补拉会把服务端全部会话灌进本地列表（CreateDialogue 只增不减），
			//   所以此时不在 social.Dialogues 里的登记项就是已删除会话 ⇒ 登记永久残留、文件无限增长。
			//   ★只在补拉到底时做：没拉到底时本地缺会话可能是网络问题，误删登记会导致漏收。
			if (load.PagedToEnd)
			{
				try
				{
					var alive = new HashSet<string>(social.Dialogues
						.Where(d => d != null && !string.IsNullOrEmpty(d._id))
						.Select(d => d._id), StringComparer.Ordinal);
					PendingAttachments.Prune(alive);
				}
				catch (Exception ex)
				{
					MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll] 清理已删除会话的登记失败（不影响功能）：" + ex.Message);
				}
			}

			// 对话列表可能并发变动，先物化成快照再遍历
			List<UpdatableChatDialogue> all = social.Dialogues.ToList();
			result.DialogTotal = all.Count;

			// 只对「可能有未领附件」的会话发请求，避免给每个空会话都打一次服务器。
			//
			// ★AttachmentsNew 是主场景唯一可靠判据（已逐行核对 4.1 源码）：
			//   UpdatableChatDialogue 构造函数 :141-178 只在 :160 赋 AttachmentsNew = info.attachmentsNew，
			//   从不赋 _hasMessagesWithRewards；
			//   而 AnyRewardMessage :113-123 在 HasMessagesWithRewards 为 false 时回退到
			//   ChatMessages.Any(...)，未点开过的会话 ChatMessages 是空的 ⇒ 恒为 false。
			//   所以「商人发东西、你还没点进那个会话」这一主场景，只有 AttachmentsNew > 0 能捕获。
			//   原版也是这么算未领附件数的：MenuTaskBar.cs:783 source.Sum(item => item.AttachmentsNew)。
			// 三个条件用 || 串联；后两个对「已打开过的会话」仍然有效，不可删。
			//
			// ★服务端载荷里的 attachmentsNew 也要并进来：
			//   它是服务端视角的权威值（ChatRoomInformation.attachmentsNew），
			//   不受本地被自动选中清零的影响。
			//
			// ★PendingAttachments 是「全部已读」的遗留补偿：
			//   「全部已读」会清零 AttachmentsNew（角标要消失），而这会让本判据失效、
			//   导致随后的「全部收取」漏收。清零之前插件已把这些会话登记进 PendingAttachments，
			//   这里把它并进候选集 ⇒ 两个按钮的先后顺序不再影响收取结果。
			var serverAttachmentIds = new HashSet<string>(load.ServerReportedAttachmentIds, StringComparer.Ordinal);

			List<UpdatableChatDialogue> candidates = all
				.Where(d => d != null && d._id != null
					&& (d.AttachmentsNew > 0
						|| d.HasMessagesWithRewards
						|| d.AnyRewardMessage
						|| serverAttachmentIds.Contains(d._id)
						|| PendingAttachments.Contains(d._id)))
				.ToList();
			result.CandidateCount = candidates.Count;
			result.AttachmentFlagCount = candidates.Count(d => d.AttachmentsNew > 0);

			if (candidates.Count == 0)
			{
				return result;
			}

			DialogueChatMessage[] collected = await FetchAttachmentsAsync(social, candidates, result).ConfigureAwait(true);

			// —— 按消息 id 去重：同一 _id 只保留第一条 ——
			var seen = new HashSet<string>(StringComparer.Ordinal);
			var merged = new List<DialogueChatMessage>();
			int raw = 0;
			int dup = 0;

			foreach (DialogueChatMessage msg in collected)
			{
				if (msg == null)
				{
					continue;
				}

				raw++;
				string key = msg._id;
				if (string.IsNullOrEmpty(key))
				{
					// 没有 id 的消息无法去重，保守保留（宁可多收一次也不漏）
					merged.Add(msg);
					continue;
				}

				if (seen.Add(key))
				{
					merged.Add(msg);
				}
				else
				{
					dup++;
				}
			}

			result.RawMessageCount = raw;
			result.DuplicateRemoved = dup;
			result.Merged = merged;
			return result;
		}

		/// <summary>
		/// 对每个候选会话并发发起取附件请求，全部回调到齐后返回**快照数组**。
		///
		/// ★用 ISocial.AllAttachmentsFromDialog（拿得到 Result）而不是 SocialNetwork 的包装，
		///   这样能区分「请求失败」与「确实没有附件」：
		///   · 失败（Succeed=false）→ 计入 FailedDialogs、**保留** PendingAttachments 登记
		///   · 成功且 messages 为空 → 记入 SettledDialogIds（可清理登记）
		///   · 成功且有附件 → 正常收取
		///
		/// ★merged 与 settled 都必须锁内快照成数组返回：
		///   超时后迟到的回调仍会在锁内写它们，交出活动 List 会让调用方的无锁枚举撞上并发修改。
		/// </summary>
		private static async Task<DialogueChatMessage[]> FetchAttachmentsAsync(
			SocialNetwork social,
			List<UpdatableChatDialogue> candidates,
			CollectResult result)
		{
			var merged = new List<DialogueChatMessage>();
			var settled = new List<string>();
			var gate = new object();
			int pending = candidates.Count;
			var tcs = new TaskCompletionSource<bool>();

			ISocial session = social._session;
			if (session == null)
			{
				MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 会话后端为空，无法取附件");
				result.FailedDialogs = candidates.Count;
				return merged.ToArray();
			}

			foreach (UpdatableChatDialogue dialogue in candidates)
			{
				string dialogId = dialogue._id;

				try
				{
					session.AllAttachmentsFromDialog(dialogId, delegate(Result<ChatMessagesList> r)
					{
						lock (gate)
						{
							try
							{
								// Result<T> 是 struct，不能与 null 比较，只能看 Succeed / Error。
								// Succeed 的定义是 Value != null && Error == null（Comfort.Common.Result.cs:26-36），
								// 所以走到 else 分支时 r.Value 必不为 null。
								if (!r.Succeed)
								{
									// ★失败：不算「已收空」，保留 PendingAttachments 登记（防永久漏收）
									result.FailedDialogs++;
									string err = string.IsNullOrEmpty(r.Error) ? "(无错误说明)" : r.Error;
									MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 会话 " + Short(dialogId) + " 取附件失败：" + err);
								}
								else
								{
									// ★补回原版包装丢掉的写回：
									//   SocialNetwork.AllAttachmentsFromDialog 在成功时会做
									//   dialog.HasMessagesWithRewards = result.Value.hasMessagesWithRewards
									//   （SocialNetwork.cs:1516-1519）。
									//   我们直调 ISocial 绕过了那个包装，必须自己写回，
									//   否则 HasMessagesWithRewards 停在旧值 ⇒ AnyRewardMessage 回退到
									//   ChatMessages 判定（UpdatableChatDialogue.cs:113-123）⇒
									//   会话里的「领取全部」按钮可能不显示。
									try
									{
										if (social.TryGetDialog(dialogId, out var target) && target != null)
										{
											target.HasMessagesWithRewards = r.Value.hasMessagesWithRewards;
										}
									}
									catch (Exception ex)
									{
										MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll] 回写会话 " + Short(dialogId) + " 的奖励标志失败（不影响功能）：" + ex.Message);
									}

									DialogueChatMessage[] messages = r.Value.messages;
									if (messages != null && messages.Length > 0)
									{
										result.FetchedDialogs++;
										merged.AddRange(messages);
									}
									else
									{
										// ★确实没有附件 ⇒ 该会话的 PendingAttachments 登记可以清掉。
										//   否则落盘文件只增不减，永远留着已经收空的会话。
										settled.Add(dialogId);
										MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll] 会话 " + Short(dialogId) + " 确认没有未领附件");
									}
								}
							}
							catch (Exception ex)
							{
								MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 处理会话 " + Short(dialogId) + " 的附件回调时异常：" + ex.Message);
							}

							pending--;
							if (pending <= 0)
							{
								tcs.TrySetResult(true);
							}
						}
					});
				}
				catch (Exception ex)
				{
					result.FailedDialogs++;
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 请求会话 " + Short(dialogId) + " 的附件时抛异常，已跳过：" + ex.Message);

					lock (gate)
					{
						pending--;
						if (pending <= 0)
						{
							tcs.TrySetResult(true);
						}
					}
				}
			}

			bool completed = await WithTimeout(tcs.Task, FetchTimeoutMs).ConfigureAwait(true);
			if (!completed)
			{
				result.FetchTimedOut = true;
				MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 取附件等待超时（" + FetchTimeoutMs + " ms），将按已收到的部分继续（可能有附件漏收）");
			}

			// ★必须在锁内快照成新数组再返回：超时后迟到的回调仍会在锁内 merged.AddRange / settled.Add，
			// 若把活动 List 交给调用方的无锁 foreach，并发修改会抛
			// InvalidOperationException: Collection was modified，把「部分成功」变成整轮失败。
			lock (gate)
			{
				result.SettledDialogIds = settled.ToArray();
				return merged.ToArray();
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
