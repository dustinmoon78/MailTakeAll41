// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Reflection;
using ChatShared;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using MailTakeAll41.Ui;
using UnityEngine;

namespace MailTakeAll41.Patches
{
	/// <summary>
	/// 挂点：EFT.UI.Chat.ChatScreen.Show(...)（4.1 真实邮件界面类，非 EftMailScreen）。
	///
	/// 职责：只负责把「全部收取」「全部已读」两个按钮挂到会话列表容器上，
	/// 业务流程分别在 TakeAllRunner / MarkAllReadRunner 里。
	///
	/// 为什么挂 Show 而不是 Update：
	///   Show 是「邮件界面被打开」的唯一入口，一局里调用次数有限，不在每帧路径上；
	///   Update 每帧都会走，往里塞建按钮逻辑违反本工作区性能红线。
	///
	/// ★严禁在此 await 任何 Task：ProcessRewards 内部 await 玩家点确认窗，
	///   在 Postfix 里 await 会死锁或吞异常（坑 3）。本 Postfix 完全同步，只负责把按钮建出来。
	/// </summary>
	internal static class ChatScreenShowPatch
	{
		private static bool _probeLogged;

		/// <summary>
		/// 显式给出完整签名，避免 nameof(Show) 命中基类重载导致 AmbiguousMatchException。
		/// 参数名 social 与目标方法一致，否则 Harmony 会报 Parameter "social" not found。
		/// </summary>
		internal static MethodBase TargetMethod()
		{
			return AccessTools.Method(
				typeof(EFT.UI.Chat.ChatScreen),
				nameof(EFT.UI.Chat.ChatScreen.Show),
				new[]
				{
					typeof(SocialNetwork),
					typeof(ProfileInfo),
					typeof(ItemController),
					typeof(Action<IEnumerable<DialogueChatMessage>, DateTime?>),
					typeof(Action<string>)
				});
		}

		internal static void Apply(Harmony harmony)
		{
			MethodBase target = TargetMethod();
			if (target == null)
			{
				throw new InvalidOperationException("找不到 ChatScreen.Show(SocialNetwork, ProfileInfo, ItemController, Action<IEnumerable<DialogueChatMessage>, DateTime?>, Action<string>) 目标方法");
			}

			MethodInfo postfix = AccessTools.Method(typeof(ChatScreenShowPatch), nameof(Postfix));
			harmony.Patch(target, postfix: new HarmonyMethod(postfix));
		}

		internal static void Postfix(SocialNetwork social)
		{
			// 存活探针：区分「挂载行」和「热路径被调用行」。
			// 只有出现本行才证明补丁真的挂上并被调用过；首次调用落 Info，其余落 Debug 控制噪声。
			if (_probeLogged)
			{
				MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll][存活探针] ChatScreen.Show 后置补丁已被调用（后续调用）");
			}
			else
			{
				_probeLogged = true;
				MailTakeAll41Plugin.Log?.LogInfo("[MailTakeAll][存活探针] ChatScreen.Show 后置补丁已挂上并被调用");
			}

			try
			{
				// 取 ChatScreen 一律用 MonoBehaviourSingleton<CommonUI>.Instance，
				// 不用 FindObjectOfType（每帧扫描全场景，性能红线）。
				var chatScreen = MonoBehaviourSingleton<EFT.UI.CommonUI>.Instance?.ChatScreen;
				if (chatScreen == null)
				{
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] CommonUI 单例或 ChatScreen 为空，本轮不挂按钮");
					return;
				}

				// 挂点：左侧会话列表容器（DialoguesContainer），按钮贴它底边。
				// 用户要求按钮独立、贴在消息框旁边，不与顶栏原版控件抢位置。
				// ★首选取会话列表；它为空时回退顶栏，保证按钮总能挂上、不至于功能整个失效。
				Transform host = chatScreen._dialoguesContainer != null
					? (Transform)chatScreen._dialoguesContainer.transform
					: (chatScreen._captionPanel != null ? chatScreen._captionPanel.transform : null);

				if (host == null)
				{
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 会话列表容器与顶栏均为空，无法放置按钮");
					return;
				}

				if (social == null)
				{
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] ChatScreen.Show 传入的 SocialNetwork 为空，按钮将不可用");
					return;
				}

				// 幂等：Show 会被反复调用，这里只会复用已有按钮，不会叠加。
				// 两个按钮并排：「全部收取」收走全部附件，「全部已读」把会话标为已读并清角标。
				TakeAllButton.Ensure(host, social);
				MarkAllReadButton.Ensure(host, social);
			}
			catch (Exception ex)
			{
				MailTakeAll41Plugin.Log?.LogError("[MailTakeAll] 挂载「全部收取」按钮时异常：" + ex);
			}
		}
	}
}
