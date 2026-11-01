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
	/// 自绘「全部已读」按钮（与「全部收取」并排，纯 RectTransform + Image + Button + TMP，无 prefab）。
	///
	/// 作用：把所有带未读/未领标记的会话一次性标为已读（服务端标记 + 本地清角标），
	/// 只清状态、不删任何会话内容（用户明确要求非破坏性）。
	/// </summary>
	internal static class MarkAllReadButton
	{
		private const string RootName = "MailTakeAll41_MarkAllReadButton";
		private const string Label = "全部已读";

		private const float BtnW = 104f;
		private const float BtnH = 26f;

		// 与「全部收取」并排：本按钮在右（+56），收取在左（-56），净间隙 8px。
		internal static readonly Vector2 Offset = new Vector2(56f, 10f);

		private static readonly Color ColBtn = new Color(0.2f, 0.22f, 0.26f, 0.96f);
		private static readonly Color ColBtnBusy = new Color(0.15f, 0.16f, 0.19f, 0.96f);
		private static readonly Color ColText = new Color(0.88f, 0.88f, 0.88f, 1f);
		private static readonly Color ColTextBusy = new Color(0.62f, 0.62f, 0.62f, 1f);

		/// <summary>幂等守卫的核心：已存在的按钮根节点。</summary>
		private static GameObject _root;

		private static Image _image;
		private static TMP_Text _label;
		private static Button _button;

		/// <summary>当前按钮监听里绑定的 SocialNetwork，只用于判断是否需要重绑。</summary>
		private static SocialNetwork _boundSocial;

		internal static void Ensure(Transform parent, SocialNetwork social)
		{
			if (parent == null)
			{
				return;
			}

			if (!ButtonFactory.PrepareSlot(parent, RootName, ref _root))
			{
				if (!ReferenceEquals(_boundSocial, social))
				{
					Bind(social);
					MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll] 「全部已读」按钮已存在且 SocialNetwork 已更换，重绑点击监听（幂等命中）");
				}
				else
				{
					MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll] 「全部已读」按钮已存在，复用（幂等命中）");
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

			MailTakeAll41Plugin.Log?.LogInfo("[MailTakeAll] 「全部已读」按钮已挂到左侧会话列表底部（与「全部收取」并排）");
		}

		private static void Bind(SocialNetwork social)
		{
			if (_button == null)
			{
				return;
			}

			SocialNetwork bound = social;

			_button.onClick.RemoveAllListeners();
			_button.onClick.AddListener(delegate
			{
				try
				{
					MarkAllReadRunner.Run(bound);
				}
				catch (Exception ex)
				{
					MailTakeAll41Plugin.Log?.LogError("[MailTakeAll] 「全部已读」按钮回调异常：" + ex);
				}
			});

			_boundSocial = social;
		}

		internal static void SetBusy(bool busy)
		{
			if (_image != null)
			{
				_image.color = busy ? ColBtnBusy : ColBtn;
			}
			if (_label != null)
			{
				_label.color = busy ? ColTextBusy : ColText;
				_label.text = busy ? "已读中…" : Label;
			}
		}
	}
}
