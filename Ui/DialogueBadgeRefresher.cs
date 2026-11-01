// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Reflection;
using ChatShared;
using EFT.UI.Chat;
using UnityEngine;

namespace MailTakeAll41.Ui
{
	/// <summary>
	/// 会话列表角标刷新器：把「未读消息 / 未领附件」两个角标直接刷掉。
	///
	/// ★为什么不用 dialogue.OnLastMessageUpdated 事件（这是本模块最重要的一条）：
	///   该事件的订阅者有两个：
	///     · DialogueView.UpdateLastMessage —— 刷角标（我们要的）
	///     · MessagesContainer 的回调 —— ScrollToTheBottom（我们不想要的）
	///   MessagesContainer 按会话缓存在 ChatScreen._instantiatedContainers 里，
	///   切换会话只隐藏不销毁、订阅一直在（ChatScreen.cs:172 / 307-324），
	///   于是「玩家在会话里翻历史时点全部已读」会把那个会话的消息列表拽回底部。
	///   而且 UpdateLastMessage 一次只刷一个角标（分支由 message.HasRewards 决定），
	///   而「从没点开过的商人会话」的 Message.HasRewards 恒为 false
	///   （UpdatableChatDialogue 构造函数与 UpdateFromAnotherItem 都不复制 HasRewards），
	///   只按原极性刷一次 ⇒ 附件角标永远不刷 ⇒ 玩家观感「点了没反应」。
	///
	/// ★本模块的做法：直接拿可见的 DialogueView 组件，用它的两个 public setter 刷角标：
	///     view.NewMessagesCount = 0;      → 写回 dialogue.New 并关掉「未读消息」角标
	///     view.NewAttachmentsMessagesCount = 0; → 写回 dialogue.AttachmentsNew 并关掉「未领附件」角标
	///   两个 setter 都是 public（DialogueView.cs:134-152），不触发任何事件 ⇒
	///   既不会滚动到底、也不会动预览文本、也不会碰事件链上的其它订阅者。
	///
	/// ★没被显示过的会话不用管：
	///   格子（cell）是被复用的，某个会话滚出可视区后它对应的 cell 可能不存在；
	///   等它再次滚进来时 DialogueView.Show 会读 dialogue.New / dialogue.AttachmentsNew
	///   （已被本插件清零）⇒ 角标天然正确。
	///
	/// ★搜索根：格子实际挂在 LightScroller 的 _targetArea 下（LightScroller.cs:414 / 732 / 738），
	///   它与 _dialoguesContainer 的祖先关系取决于 UI prefab 装配、反编译源码里无法静态证明。
	///   ⇒ 传多个根，任一根命中即可；都不命中时由调用方打 Warning 便于实机对账。
	///
	/// ★性能：只在玩家点击时跑一次，不是每帧路径。
	///   反射取 DialogueView._dialogue（private 字段）只解析一次并静态缓存。
	/// </summary>
	internal static class DialogueBadgeRefresher
	{
		/// <summary>DialogueView 的私有字段 _dialogue（UpdatableChatDialogue），只解析一次。</summary>
		private static FieldInfo _dialogueField;
		private static bool _fieldProbed;

		/// <summary>
		/// 把给定会话在列表里的角标刷掉（按多个搜索根依次找，任一根命中即可）。
		/// 返回成功刷新的格子数（0 不代表失败，可能该会话当前不可见）。
		/// </summary>
		internal static int Refresh(List<Transform> roots, List<UpdatableChatDialogue> targets)
		{
			if (roots == null || roots.Count == 0 || targets == null || targets.Count == 0)
			{
				return 0;
			}

			var wanted = new HashSet<UpdatableChatDialogue>(targets);
			var done = new HashSet<UpdatableChatDialogue>();
			int refreshed = 0;

			foreach (Transform root in roots)
			{
				if (root == null)
				{
					continue;
				}

				DialogueView[] views;
				try
				{
					views = root.GetComponentsInChildren<DialogueView>(true);
				}
				catch (Exception ex)
				{
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 遍历会话列表格子失败：" + ex.Message);
					continue;
				}

				if (views == null)
				{
					continue;
				}

				foreach (DialogueView view in views)
				{
					// Unity 假 null：格子可能已被回收
					if (view == null)
					{
						continue;
					}

					try
					{
						UpdatableChatDialogue dialogue = GetDialogue(view);
						if (dialogue == null || !wanted.Contains(dialogue) || !done.Add(dialogue))
						{
							continue;
						}

						// 两个角标各刷一次：这两个 setter 都会把值写回 dialogue 对应字段，
						// 传 0 与本插件「先清零」的结果一致，不会把计数复活。
						view.NewMessagesCount = 0;
						view.NewAttachmentsMessagesCount = 0;
						refreshed++;
					}
					catch (Exception ex)
					{
						MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll] 刷新单个会话角标失败（不影响功能）：" + ex.Message);
					}
				}
			}

			return refreshed;
		}

		/// <summary>取格子绑定的会话对象。</summary>
		private static UpdatableChatDialogue GetDialogue(DialogueView view)
		{
			if (!_fieldProbed)
			{
				_fieldProbed = true;
				try
				{
					_dialogueField = typeof(DialogueView).GetField("_dialogue", BindingFlags.NonPublic | BindingFlags.Instance);
				}
				catch (Exception ex)
				{
					MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 解析 DialogueView._dialogue 失败：" + ex.Message);
				}
			}

			if (_dialogueField == null)
			{
				return null;
			}

			return _dialogueField.GetValue(view) as UpdatableChatDialogue;
		}
	}
}
