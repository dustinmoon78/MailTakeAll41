// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using TMPro;
using UnityEngine;

namespace MailTakeAll41.Ui
{
	/// <summary>
	/// 自绘 TMP 字体的三级兜底解析，**静态缓存、只探测一次**，不在每帧路径上做反射或 FindObjectsOfTypeAll。
	///
	/// 为什么需要它：直接 AddComponent&lt;TextMeshProUGUI&gt;() 而不给 font，TMP 会拿不到默认字体，
	/// 表现为「按钮在、字是空的」。这是无 prefab 自绘 UGUI 的头号坑。
	/// 优先借用邮件界面顶栏里现成文本组件的字体（就是游戏自己那套，含中文字形），
	/// 拿不到再退到 TMP_Settings 默认字体，最后才全资源扫描（只扫一次）。
	/// </summary>
	internal static class UiFontCache
	{
		private static TMP_FontAsset _font;
		private static bool _probed;

		/// <summary>从指定根节点下借一个现成 TMP 组件的字体；借不到返回 null 且不置缓存，允许下一级兜底。</summary>
		internal static TMP_FontAsset TryBorrowFrom(Transform root)
		{
			if (root == null)
			{
				return null;
			}

			try
			{
				var tmp = root.GetComponentInChildren<TMP_Text>(true);
				if (tmp != null && tmp.font != null)
				{
					return tmp.font;
				}
			}
			catch (Exception)
			{
				// 借用失败不是致命错误，继续往下走兜底
			}

			return null;
		}

		internal static TMP_FontAsset Get()
		{
			if (_font != null)
			{
				return _font;
			}

			// 一级：TMP 全局默认字体
			try
			{
				if (TMP_Settings.defaultFontAsset != null)
				{
					_font = TMP_Settings.defaultFontAsset;
					return _font;
				}
			}
			catch (Exception)
			{
			}

			// 二级：全资源扫描取第一个字体资产 —— 只做一次，之后走静态缓存
			if (!_probed)
			{
				_probed = true;
				try
				{
					var all = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
					if (all != null && all.Length > 0)
					{
						_font = all[0];
					}
				}
				catch (Exception)
				{
				}
			}

			return _font;
		}
	}
}