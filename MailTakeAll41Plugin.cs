// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using EFT.UI.Chat;
using HarmonyLib;
using MailTakeAll41.Patches;

namespace MailTakeAll41
{
	/// <summary>
	/// 一键全收邮件 MailTakeAll —— EFT 4.1 纯客户端 BepInEx 插件。
	///
	/// 功能：在局外邮件界面（EFT.UI.Chat.ChatScreen）左侧会话列表底部并排追加两个按钮：
	///   「全部收取」—— 一次性把所有会话里所有未领附件合并去重后，走原版 ProcessRewards 流程；
	///   「全部已读」—— 把所有带未读/未领标记的会话一次性标为已读（只清状态，不删内容）。
	/// 「全部收取」一次性把所有会话里所有未领附件合并去重后，走原版 ProcessRewards 流程，
	/// 由玩家在原版传送界面（TransferItemsScreen）再点「接收全部」入库。
	///
	/// 三个必须避开的坑（均在代码里有对应规避，详见 Patches/ChatScreenShowPatch.cs 与 Services/ 下注释）：
	///   1. 绝不循环调 ChatScreen.TransferAll —— 第一次调用就会 Close() 聊天并开传送界面，
	///      后续调用被 MainMenuShowOperation 里 CheckCurrentScreen(EEftScreenType.TransferItems)
	///      静默丢弃。正确做法是合并成一个数组后只调一次 ProcessRewards。
	///   2. 会话列表每页只拉 30 条，玩家没滚过左侧列表时未加载的会话不在 Dialogues 里，
	///      必须先循环调 UpdateDialogueList 补拉，否则会漏收。
	///   3. ProcessRewards 是 async Task，内部 await 玩家点确认窗，Harmony Postfix 里绝不能 await，
	///      全程 fire-and-forget。
	/// </summary>
	// ★正式发布口径（工作区铁律）：正式版显示名去掉「BE 版 N」，版本串恒为线上正式号、纯 x.y.z
	//   （带后缀会被 BepInEx 解析成 null 并整个跳过插件）。
	//   本插件从未对外发布过，1.0.0 始终是线上正式号 ⇒ 正式版仍为 1.0.0
	//   （内测期那个 1.1.0 是自用号，不占正式序列）。
	[BepInPlugin("com.dsh.mailtakeall41", "一键全收邮件 MailTakeAll", "1.0.0")]
	public sealed class MailTakeAll41Plugin : BaseUnityPlugin
	{
		internal static ManualLogSource Log;

		private void Awake()
		{
			// 启动默认只留极少几行：这一行版本 + 下面一行挂载结果。逐条明细一律走 LogDebug。
			Log = Logger;

			Harmony harmony = null;
			try
			{
				harmony = new Harmony("com.dsh.mailtakeall41");
				ChatScreenShowPatch.Apply(harmony);
			}
			catch (Exception ex)
			{
				// Warning/Error 永不静默：挂载失败必须可见，否则表现为「插件装了但没反应」
				Log.LogError("[MailTakeAll] Harmony 挂载失败，插件不会生效：" + ex);
				return;
			}

			int patched = 0;
			try
			{
				foreach (MethodBase ignored in harmony.GetPatchedMethods())
				{
					patched++;
				}
			}
			catch (Exception ex)
			{
				Log.LogWarning("[MailTakeAll] 统计已挂载补丁数失败：" + ex.Message);
			}

			Log.LogInfo("[MailTakeAll] v1.0.0 已加载，补丁挂载结果：" + patched + " 处（目标 ChatScreen.Show 后置）");
		}
	}
}