// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Threading.Tasks;
using ChatShared;
using EFT;
using EFT.UI.Chat;
using MailTakeAll41.Ui;

namespace MailTakeAll41.Services
{
	/// <summary>
	/// 一次点击跑一轮的编排器：防重入 → 收集 → 合并去重 → **只调一次** ProcessRewards → fire-and-forget。
	///
	/// ★防重入闸与「全部已读」共用（OperationGate）：
	///   两个按钮并排，玩家可能同时点。两个流程会同时改 dialogue.New / dialogue.AttachmentsNew，
	///   「全部已读」清零附件标记会让「全部收取」的候选判据失效 ⇒ 漏收附件。故全插件同一时刻只跑一个批量流程。
	/// </summary>
	internal static class TakeAllRunner
	{
		internal static void Run(SocialNetwork social)
		{
			if (!OperationGate.TryEnter("全部收取"))
			{
				return;
			}

			TakeAllButton.SetBusy(true);

			// ★fire-and-forget：绝不 await。ProcessRewards 内部 await 玩家点确认窗，
			//   在任何 Harmony 补丁里 await 都会死锁或吞异常（坑 3）。
			Task task = RunAsync(social);
			if (task == null)
			{
				OperationGate.Exit();
				TakeAllButton.SetBusy(false);
				MailTakeAll41Plugin.Log?.LogError("[MailTakeAll] 启动收取流程失败：返回了 null Task");
				return;
			}

			task.ContinueWith(
				t =>
				{
					AggregateException ex = t.Exception;
					if (ex != null)
					{
						// 未观察到的 task 异常会被 Unity 静默吞掉，这里显兜住，保证永不静默
						MailTakeAll41Plugin.Log?.LogError("[MailTakeAll] 收取流程异常：" + ex);
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
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] SocialNetwork 为空，无法收取");
					return;
				}

				// 取 ChatScreen 一律用 MonoBehaviourSingleton，不用 FindObjectOfType（性能红线）
				var chatScreen = MonoBehaviourSingleton<EFT.UI.CommonUI>.Instance?.ChatScreen;
				if (chatScreen == null)
				{
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 找不到 ChatScreen，无法收取");
					return;
				}

				CollectResult result = await DialogueCollector.CollectAsync(social).ConfigureAwait(true);

				MailTakeAll41Plugin.Log?.LogInfo(
					"[MailTakeAll] 收集完成：会话总数 " + result.DialogTotal +
					"，有奖励会话 " + result.CandidateCount +
					"，其中未领附件标记命中 " + result.AttachmentFlagCount +
					"，取到附件会话 " + result.FetchedDialogs +
					"，失败 " + result.FailedDialogs +
					"，附件消息 " + result.RawMessageCount +
					"，去重丢弃 " + result.DuplicateRemoved +
					"，最终 " + (result.Merged == null ? 0 : result.Merged.Count) + " 条");

				if (result.FailedDialogs > 0 || result.FetchTimedOut || result.PagingIncomplete)
				{
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 本轮存在不完整情况，请留意上面的失败/超时日志");
				}

				// ★清理「已确认没有可领附件」的会话登记：
				//   PendingAttachments 是「全部已读」的遗留补偿（保住收取候选判据），
				//   但落盘文件若只增不减，会永远留着早已收空的会话 ⇒ 每次收取都白打一次请求。
				//   这里在取附件阶段确认该会话已无附件后就清登记（见 DialogueCollector.SettledDialogIds）。
				foreach (string settledId in result.SettledDialogIds)
				{
					PendingAttachments.Remove(settledId);
				}

				if (result.Merged == null || result.Merged.Count == 0)
				{
					// 空数组直接喂给 ProcessRewards 会被它首行判空静默 return，必须给可见日志
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 没有可收取的附件（合并后为空）");
					return;
				}

				// ★★坑 1 的正解：合并成一个数组后**只调一次** ProcessRewards。
				//   绝不能循环调 ChatScreen.TransferAll(dialog)：第一次调用就会 chatPanel.Close()
				//   并打开 TransferItemsScreen，之后每次调用都会被
				//   MainMenuShowOperation 里 CheckCurrentScreen(EEftScreenType.TransferItems) 守卫静默丢弃，
				//   不抛异常、不报错，表现为「按了没反应」。
				Task reward = chatScreen.ProcessRewards(result.Merged.ToArray());
				if (reward == null)
				{
					MailTakeAll41Plugin.Log?.LogError("[MailTakeAll] ProcessRewards 返回了 null Task");
					return;
				}

				// ProcessRewards 内部还会 await 任务物品确认窗与档案奖励窗，这里继续不 await
				reward.ContinueWith(
					t =>
					{
						AggregateException ex = t.Exception;
						if (ex != null)
						{
							MailTakeAll41Plugin.Log?.LogError("[MailTakeAll] ProcessRewards 抛出异常：" + ex);
						}
						else
						{
							MailTakeAll41Plugin.Log?.LogInfo("[MailTakeAll] ProcessRewards 已正常走完");
						}
					},
					TaskContinuationOptions.ExecuteSynchronously);

				MailTakeAll41Plugin.Log?.LogInfo("[MailTakeAll] 已提交 " + result.Merged.Count + " 条附件到原版传送界面");
			}
			catch (Exception ex)
			{
				MailTakeAll41Plugin.Log?.LogError("[MailTakeAll] 收取流程失败：" + ex);
			}
			finally
			{
				OperationGate.Exit();
				TakeAllButton.SetBusy(false);
			}
		}
	}
}
