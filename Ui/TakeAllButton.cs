// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using EFT;
using MailTakeAll41.Services;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MailTakeAll41.Ui
{
	/// <summary>
	/// 自绘「全部收取」按钮（纯 RectTransform + Image + Button + TMP，无 prefab）。
	///
	/// ★零 IMGUI：邮件界面是纯 UGUI，全项目 EFT.UI/ 与 EFT.UI.Screens/ 下 OnGUI 命中数为 0，
	///   所以不能用 GUI.* 系列 API（那些还只能在 OnGUI 内调用，见项目记忆）。
	/// </summary>
	internal static class TakeAllButton
	{
		private const string RootName = "MailTakeAll41_TakeAllButton";
		private const string Label = "全部收取";

		private const float BtnW = 104f;
		private const float BtnH = 26f;

		// 贴左侧会话列表底部（列表下方、输入框上方）。
		// ★用户要求：按钮独立出来贴在消息框旁边，不挤顶栏（顶栏已有原版标题/好友/设置等控件）。
		// ★与「全部已读」并排：本按钮在左（-56），已读在右（+56），两按钮中线间距 112 ⇒ 净间隙 8px。
		//   坐标未实机校准，是本插件最主要的剩余风险点。
		internal static readonly Vector2 Offset = new Vector2(-56f, 10f);

		private static readonly Color ColBtn = new Color(0.2f, 0.22f, 0.26f, 0.96f);
		private static readonly Color ColBtnBusy = new Color(0.15f, 0.16f, 0.19f, 0.96f);
		private static readonly Color ColText = new Color(0.88f, 0.88f, 0.88f, 1f);
		private static readonly Color ColTextBusy = new Color(0.62f, 0.62f, 0.62f, 1f);

		/// <summary>幂等守卫的核心：已存在的按钮根节点。</summary>
		private static GameObject _root;

		private static Image _image;
		private static TMP_Text _label;
		private static Button _button;

		/// <summary>当前按钮监听里绑定的 SocialNetwork，只用于判断是否需要重绑（已逐行核对 ClientBackendSession.cs:1378）。</summary>
		private static SocialNetwork _boundSocial;

		/// <summary>
		/// 幂等挂载。Show 会被反复调用（玩家反复开关邮件界面），
		/// 所以必须保证：同一父节点下永远只有一个按钮，重复调用只更新不新建。
		/// </summary>
		internal static void Ensure(Transform parent, SocialNetwork social)
		{
			if (parent == null)
			{
				return;
			}

			// PrepareSlot 返回 false = 已有同父节点的按钮，直接复用（social 换了才重绑）
			if (!ButtonFactory.PrepareSlot(parent, RootName, ref _root))
			{
				if (!ReferenceEquals(_boundSocial, social))
				{
					Bind(social);
					MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll] 「全部收取」按钮已存在且 SocialNetwork 已更换，重绑点击监听（幂等命中）");
				}
				else
				{
					MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll] 「全部收取」按钮已存在，复用（幂等命中）");
				}

				return;
			}

			Build(parent, social);
		}

		private static void Build(Transform parent, SocialNetwork social)
		{
			ButtonFactory.Built built = ButtonFactory.Create(
				parent,
				RootName,
				Label,
				BtnW,
				BtnH,
				Offset,
				ColBtn,
				ColText,
				14f,
				parent);

			_root = built.Root;
			_image = built.Image;
			_label = built.Label;
			_button = built.Button;

			Bind(social);

			MailTakeAll41Plugin.Log?.LogInfo("[MailTakeAll] 「全部收取」按钮已挂到左侧会话列表底部（与「全部已读」并排）");
		}

		/// <summary>
		/// 把点击监听绑到指定的 SocialNetwork 上。
		/// ★UnityEvent 必须先 RemoveAllListeners 再 Add，否则多次调用会叠加出多个监听，
		///   一次点击触发多轮流程（还有重入闸挡着，但会刷一串无意义日志）。
		/// </summary>
		private static void Bind(SocialNetwork social)
		{
			if (_button == null)
			{
				return;
			}

			SocialNetwork bound = social;

			// 回调里绝不能抛到 Unity 主线程之外，也不 await：整轮流程由 TakeAllRunner 托管
			_button.onClick.RemoveAllListeners();
			_button.onClick.AddListener(delegate
			{
				try
				{
					TakeAllRunner.Run(bound);
				}
				catch (Exception ex)
				{
					MailTakeAll41Plugin.Log?.LogError("[MailTakeAll] 按钮回调异常：" + ex);
				}
			});

			_boundSocial = social;
		}

		/// <summary>运行中置灰，给玩家一个「已经在跑了」的可见反馈，同时靠 _running 真正防重入。</summary>
		internal static void SetBusy(bool busy)
		{
			if (_image != null)
			{
				_image.color = busy ? ColBtnBusy : ColBtn;
			}
			if (_label != null)
			{
				_label.color = busy ? ColTextBusy : ColText;
				_label.text = busy ? "收取中…" : Label;
			}
		}
	}
}
