// SPDX-License-Identifier: AGPL-3.0-or-later
using System;

namespace MailTakeAll41.Services
{
	/// <summary>
	/// 「全部收取」与「全部已读」共用的重入闸。
	///
	/// ★为什么要共用一个闸（而不是两个 runner 各自一份）：
	///   两个按钮并排，玩家可能同时点。两个流程会同时改 dialogue.New / dialogue.AttachmentsNew、
	///   同时读写 social.ReadDialoguesQueue 与会话列表，叠加后会出现
	///   「全部已读」清掉附件标记 ⇒ 「全部收取」候选集为空 ⇒ 一件都收不到。
	///   所以整个插件同一时刻只允许一个批量流程在跑。
	///
	/// ★占用超时兜底（本次新增）：
	///   闸的常规释放点在两个 runner 的 finally 里，但「Task 永不完成」是这类流程已知的失败形态
	///   （fire-and-forget 的 await 链一旦被吞掉，finally 就永远不执行）—— 那会把两个按钮一起锁死，
	///   只能重启游戏。故记录占用起始时刻，超过 StaleSeconds 仍未释放即强制放闸并打一条 Error：
	///   宁可让两个流程重叠一次（代价可见、可恢复），也不让按钮永久失效。
	/// </summary>
	internal static class OperationGate
	{
		/// <summary>占用超过该秒数视为流程卡死，下次点击时强制放闸。</summary>
		internal const float StaleSeconds = 90f;

		private static bool _busy;
		private static float _busySince;

		/// <summary>占用已持续多少秒（未占用时为 0）；供日志与实机对账。</summary>
		internal static float BusyForSeconds
		{
			get { return _busy ? UnityEngine.Time.realtimeSinceStartup - _busySince : 0f; }
		}

		/// <summary>占用闸；已被占用时返回 false（调用方应当直接忽略这次点击）。</summary>
		internal static bool TryEnter(string what)
		{
			if (_busy)
			{
				float held = UnityEngine.Time.realtimeSinceStartup - _busySince;
				if (held <= StaleSeconds)
				{
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 另一个批量流程（「" + what + "」）还在跑，本次点击已忽略");
					return false;
				}

				// 看门狗：finally 没跑到（Task 永不完成 / 异常被吞）⇒ 强制放闸，避免按钮永久失效
				MailTakeAll41Plugin.Log?.LogError("[MailTakeAll] 上一个批量流程已占用 " + held.ToString("F1")
					+ " 秒（上限 " + StaleSeconds + " 秒），判定为卡死，已强制释放占用闸");
				_busy = false;
				_busySince = 0f;
			}

			_busy = true;
			_busySince = UnityEngine.Time.realtimeSinceStartup;
			return true;
		}

		internal static void Exit()
		{
			_busy = false;
			_busySince = 0f;
		}

		internal static bool IsBusy
		{
			get { return _busy; }
		}
	}
}
