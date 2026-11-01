// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace MailTakeAll41.Ui
{
	/// <summary>
	/// 自绘 UGUI 的公共构件：按钮工厂 + 幂等挂载守卫 + 文本构造。
	///
	/// ★零 IMGUI：邮件界面是纯 UGUI（全项目 EFT.UI/ 与 EFT.UI.Screens/ 下 OnGUI 命中数为 0），
	///   所以只能用 UGUI + TMP，不能用 GUI.* 系列 API（那些还只能在 OnGUI 内调用，见项目记忆）。
	/// </summary>
	internal static class ButtonFactory
	{
		/// <summary>造好的按钮：根节点 + 底图 + 文本 + 按钮组件。</summary>
		internal sealed class Built
		{
			internal GameObject Root;
			internal Image Image;
			internal TMP_Text Label;
			internal Button Button;
		}

		/// <summary>
		/// 幂等挂载守卫。
		///   返回 true  = 需要新建（旧的已清理干净）
		///   返回 false = 已有同父节点下的可复用根节点，调用方只管重绑监听
		///
		/// ★为什么不能只比父节点就当复用：SocialNetwork 每次 session 初始化都重建
		///   （ClientBackendSession.cs:1378 _socialNetwork = new SocialNetwork()），
		///   而挂点容器在 CommonUI 上（整个应用生命周期只建一次）。
		///   打完一局撤离后父节点不变但 social 已是新对象，若沿用旧闭包，
		///   请求会打向上一局已释放的 _session，表现为干等超时。
		///   所以复用时必须由调用方按最新 social 重绑监听。
		/// </summary>
		internal static bool PrepareSlot(Transform parent, string rootName, ref GameObject cached)
		{
			if (cached != null && cached.transform.parent == parent)
			{
				return false;
			}

			// 记录存在但父节点变了（邮件界面被重建）⇒ 先销毁旧的再建新的，避免留下孤儿按钮
			if (cached != null)
			{
				DestroySafely(cached);
				cached = null;
			}

			// 兜底：静态引用失效（如域重载）时按名字扫一遍，防止出现两个同名按钮
			GameObject stale = FindByName(parent, rootName);
			if (stale != null)
			{
				DestroySafely(stale);
			}

			return true;
		}

		/// <summary>
		/// 造一个按钮。
		/// fontSource 传宿主节点（邮件界面容器），用来借用游戏自己的 TMP 字体（含中文字形）。
		/// </summary>
		internal static Built Create(
			Transform parent,
			string rootName,
			string label,
			float width,
			float height,
			Vector2 anchoredPosition,
			Color background,
			Color textColor,
			float fontSize,
			Transform fontSource)
		{
			var go = new GameObject(rootName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
			var rt = (RectTransform)go.transform;
			rt.SetParent(parent, false);
			rt.anchorMin = new Vector2(0.5f, 0f);
			rt.anchorMax = new Vector2(0.5f, 0f);
			rt.pivot = new Vector2(0.5f, 0f);
			rt.sizeDelta = new Vector2(width, height);
			rt.anchoredPosition = anchoredPosition;

			Image image = go.GetComponent<Image>();
			image.color = background;
			image.raycastTarget = true;

			Button button = go.GetComponent<Button>();
			button.targetGraphic = image;

			TMP_Text text = MakeText(rt, label, fontSize, textColor, fontSource);
			Fill(text.rectTransform);

			return new Built
			{
				Root = go,
				Image = image,
				Label = text,
				Button = button
			};
		}

		/// <summary>
		/// 自绘 TMP 文本。
		/// ★头号坑：不给字体就是一片空白。三级兜底：
		///   1) 借宿主节点里现成文本组件的字体（游戏自己那套，含中文字形）
		///   2) TMP_Settings 默认字体
		///   3) 全资源扫描（只扫一次，见 UiFontCache）
		/// </summary>
		internal static TMP_Text MakeText(RectTransform parent, string content, float size, Color color, Transform fontSource)
		{
			var go = new GameObject("Label", typeof(RectTransform));
			((RectTransform)go.transform).SetParent(parent, false);
			go.AddComponent<CanvasRenderer>();
			var tmp = go.AddComponent<TextMeshProUGUI>();

			TMP_FontAsset font = UiFontCache.TryBorrowFrom(fontSource);
			if (font == null)
			{
				font = UiFontCache.Get();
			}

			if (font != null)
			{
				tmp.font = font;
			}
			else
			{
				MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 未取到任何 TMP 字体，按钮可能显示为空文字（功能不受影响）");
			}

			tmp.text = content;
			tmp.fontSize = size;
			tmp.color = color;
			tmp.alignment = TextAlignmentOptions.Center;
			tmp.enableWordWrapping = false;
			tmp.overflowMode = TextOverflowModes.Overflow;
			tmp.raycastTarget = false;
			return tmp;
		}

		/// <summary>让文本铺满父物体。</summary>
		internal static void Fill(RectTransform rt)
		{
			rt.anchorMin = Vector2.zero;
			rt.anchorMax = Vector2.one;
			rt.pivot = new Vector2(0.5f, 0.5f);
			rt.offsetMin = Vector2.zero;
			rt.offsetMax = Vector2.zero;
		}

		internal static GameObject FindByName(Transform parent, string name)
		{
			try
			{
				for (int i = 0; i < parent.childCount; i++)
				{
					var child = parent.GetChild(i);
					if (child != null && child.name == name)
					{
						return child.gameObject;
					}
				}
			}
			catch (Exception)
			{
			}

			return null;
		}

		internal static void DestroySafely(GameObject go)
		{
			if (go == null)
			{
				return;
			}

			try
			{
				UnityEngine.Object.Destroy(go);
			}
			catch (Exception ex)
			{
				MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 销毁旧按钮失败：" + ex.Message);
			}
		}
	}
}
