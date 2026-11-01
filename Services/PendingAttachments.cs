// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MailTakeAll41.Services
{
	/// <summary>
	/// 「本会话内已知仍有未领附件」的会话 id 集合（进程内 + 落盘）。
	///
	/// ★为什么需要它（对应审查 P1-3）：
	///   「全部收取」的候选判据里，AttachmentsNew > 0 是**没点开过的会话**唯一可靠的信号
	///   （构造函数只赋 AttachmentsNew，从不赋 _hasMessagesWithRewards；
	///   AnyRewardMessage 回退到空的 ChatMessages ⇒ 两者恒 false）。
	///   而「全部已读」要把角标清零，角标清零就会把 AttachmentsNew 一起清掉，
	///   于是「先点全部已读、再点全部收取」会一个附件都收不到。
	///
	/// 折中：清角标之前先把「有未领附件」的会话 id 记进这个集合，
	///   「全部收取」的候选集再加上「本集合里有的会话」⇒ 两个按钮的先后顺序不再影响收取结果。
	///
	/// ★为什么落盘（对应审查 P2-3 的跨进程失效）：
	///   只放内存的话，重进游戏后集合为空。若服务端确实随 read 请求清掉了 attachmentsNew
	///   （★客户端源码查不到，属未知项），该会话既无角标、也进不了「全部收取」候选
	///   ⇒ 未领附件只能手动进会话取。
	///   落盘后重进游戏仍能找回这批会话，收取不受影响。
	///
	/// ★落盘位置：插件所在目录（与 BossNotifierZh41 取插件目录同一手法，
	///   不能用 Paths.PluginPath —— 那是 BepInEx\plugins\ 根目录，会把文件丢在所有插件中间）。
	/// ★写盘失败一律不抛：读不到就用空集合，写不了就只留内存版，功能不受影响。
	/// </summary>
	internal static class PendingAttachments
	{
		private static readonly HashSet<string> Ids = new HashSet<string>(StringComparer.Ordinal);

		private static string _filePath;
		private static bool _loaded;

		/// <summary>插件目录下的持久化文件名。</summary>
		private const string FileName = "MailTakeAll41.pending-attachments.txt";

		/// <summary>记下一个「有未领附件」的会话。</summary>
		internal static void Add(string dialogId)
		{
			if (string.IsNullOrEmpty(dialogId))
			{
				return;
			}

			Load();

			if (Ids.Add(dialogId))
			{
				Save();
			}
		}

		internal static bool Contains(string dialogId)
		{
			if (string.IsNullOrEmpty(dialogId))
			{
				return false;
			}

			Load();
			return Ids.Contains(dialogId);
		}

		/// <summary>会话被移除（玩家删了会话）时清掉登记，避免集合无限增长。</summary>
		internal static void Remove(string dialogId)
		{
			if (string.IsNullOrEmpty(dialogId))
			{
				return;
			}

			Load();

			if (Ids.Remove(dialogId))
			{
				Save();
			}
		}

		/// <summary>
		/// 清掉不在「存活会话集合」里的登记（玩家已删除的会话）。
		///
		/// ★为什么需要：PendingAttachments 的登记只在「确认该会话确实没有附件」时才会被清掉，
		///   而玩家删掉一个会话后，它再也不会出现在 social.Dialogues 里 ⇒ 永远走不到那个分支
		///   ⇒ 落盘文件里的 id 无限累积。
		/// ★调用时机：只在会话列表**已补拉到底**时调用（此时本地列表是全量的）。
		///   没拉到底就调用会把「暂时没加载到」的会话误删 ⇒ 可能漏收。
		/// </summary>
		internal static void Prune(HashSet<string> aliveIds)
		{
			if (aliveIds == null)
			{
				return;
			}

			Load();

			List<string> dead = null;
			foreach (string id in Ids)
			{
				if (!aliveIds.Contains(id))
				{
					if (dead == null)
					{
						dead = new List<string>();
					}

					dead.Add(id);
				}
			}

			if (dead == null || dead.Count == 0)
			{
				return;
			}

			foreach (string id in dead)
			{
				Ids.Remove(id);
			}

			Save();
			MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll] 清理了 " + dead.Count + " 个已删除会话的待收登记");
		}

		/// <summary>清空（换局或重置时调用）。</summary>
		internal static void Clear()
		{
			Load();
			Ids.Clear();
			Save();
		}

		internal static int Count
		{
			get
			{
				Load();
				return Ids.Count;
			}
		}

		/// <summary>首次访问时把磁盘上的登记读进来（只读一次，之后走内存）。</summary>
		private static void Load()
		{
			if (_loaded)
			{
				return;
			}

			_loaded = true;

			try
			{
				string path = GetFilePath();
				if (path == null || !File.Exists(path))
				{
					return;
				}

				foreach (string line in File.ReadAllLines(path))
				{
					string trimmed = line.Trim();
					if (trimmed.Length > 0)
					{
						Ids.Add(trimmed);
					}
				}

				MailTakeAll41Plugin.Log?.LogDebug("[MailTakeAll] 从磁盘恢复了 " + Ids.Count + " 个待收取会话的登记");
			}
			catch (Exception ex)
			{
				MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 读取待收取会话登记失败（不影响功能）：" + ex.Message);
			}
		}

		/// <summary>把登记写回磁盘（写失败只警告，不影响功能）。</summary>
		private static void Save()
		{
			try
			{
				string path = GetFilePath();
				if (path == null)
				{
					return;
				}

				string dir = Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
				{
					Directory.CreateDirectory(dir);
				}

				File.WriteAllLines(path, Ids.ToArray());
			}
			catch (Exception ex)
			{
				MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 写待收取会话登记失败（不影响功能）：" + ex.Message);
			}
		}

		/// <summary>插件所在目录下的登记文件路径。</summary>
		private static string GetFilePath()
		{
			if (!string.IsNullOrEmpty(_filePath))
			{
				return _filePath;
			}

			try
			{
				string asmPath = typeof(PendingAttachments).Assembly.Location;
				if (string.IsNullOrEmpty(asmPath))
				{
					return null;
				}

				string dir = Path.GetDirectoryName(asmPath);
				if (string.IsNullOrEmpty(dir))
				{
					return null;
				}

				_filePath = Path.Combine(dir, FileName);
				return _filePath;
			}
			catch (Exception ex)
			{
				MailTakeAll41Plugin.Log?.LogWarning("[MailTakeAll] 解析插件目录失败：" + ex.Message);
				return null;
			}
		}
	}
}
