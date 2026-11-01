# 一键全收邮件 MailTakeAll

EFT 4.1 纯客户端 BepInEx 插件。在局外邮件界面（EFT.UI.Chat.ChatScreen）左侧会话列表底部**并排两个按钮**：

- **「全部收取」**：一次收取**所有会话的所有未领附件**，随后走**原版传送界面**（TransferItemsScreen），
  由玩家再点「接收全部」入库。**不做静默入库，不需要服务端 mod。**
- **「全部已读」**：把所有带未读/未领标记的会话一次性**标为已读**（服务端标记 + 本地清角标）。
  **只清状态，不删任何会话、不删任何消息。**

- GUID：`com.dsh.mailtakeall41`
- 显示名：一键全收邮件 MailTakeAll BE 版 2
- 版本：1.1.0（★BepInPlugin 的 Version 必须纯 x.y.z，带后缀会被解析成 null 并整个跳过插件）
- 目标框架：netstandard2.1

## 界面与流程

```
邮件界面(ChatScreen) 左侧会话列表底部 [全部收取] [全部已读]
        ↓ 点「全部收取」
补拉全量会话列表（共用 DialogueLoader）
        ↓
逐个会话 AllAttachmentsFromDialog 取附件
        ↓
按 DialogueChatMessage._id 去重合并成一个数组
        ↓
ProcessRewards(合并数组)   ← 全程只调一次
        ↓
原版 TransferItemsScreen → 玩家点「接收全部」→ 逐件 QuickFindAppropriatePlace 入库
        ↓
仓库塞不下的留在 _temporaryGrid，下次可继续
        ↓
已确认无附件的会话，从 PendingAttachments 登记里清掉

        ↓ 点「全部已读」
补拉全量会话列表（共用 DialogueLoader）
        ↓
目标集合 = 本地 New>0/AttachmentsNew>0 的会话 ∪ 服务端载荷报告有未读的会话
        ↓
把有未领附件的会话登记进 PendingAttachments（保住「全部收取」的候选判据）
        ↓
直接调 _session.ChatReadDialogues(ids) → POST {messaging}/client/mail/dialog/read
   ★不经过 social.ReadDialoguesQueue，避免回执 Clear 抹掉原版新入队的条目
        ↓
本地 dialogue.New / dialogue.AttachmentsNew 清零
        ↓
直接刷可见格子的两个角标（不走事件，避免把玩家翻历史拽回底部）
        ↓
MenuTaskBar.SetNewMessagesCount() 刷新主菜单未读总数
```

## 「全部已读」的 4.1 机制（逐条核对反编译源码）

| 事实 | 出处 |
|---|---|
| 未读数存放在 `UpdatableChatDialogue.New`（未读消息）与 `.AttachmentsNew`（未领附件），都是 public int 字段 | ChatShared/UpdatableChatDialogue.cs:67-69 |
| 服务端标记已读 = `POST {messaging}/client/mail/dialog/read` | EFT/ClientBackendSession.cs:2540-2548 |
| 原版 `SocialNetwork.ReadDialogues()` 在**回执回调里** `ReadDialoguesQueue.Clear()` ⇒ 不可共用 | EFT/SocialNetwork.cs:1421-1435 |
| `Backend.SendVoid` 成功失败都会回调 ⇒ 必须按 `IResult.Succeed` 判定 | Backend.cs:241-256 |
| 原版 `AddDialogueToReadQueue` 门槛是 `New>0 && AttachmentsNew!=0 && Type!=GlobalChat` | EFT/SocialNetwork.cs:1437-1443 |
| 原版清零未读靠「玩家点开会话」：`DialogueView.SelectView` 与 `MenuTaskBar.DialogueSelectedHandler` | EFT.UI.Chat/DialogueView.cs:348-357；EFT.UI/MenuTaskBar.cs:769-777 |
| 左侧格子只在 Show / UpdateLastMessage / SelectView 时重读那两个字段 ⇒ 改字段后必须主动刷新显示 | EFT.UI.Chat/DialogueView.cs:134-152, 310-324 |
| `UpdateLastMessage` 一次只刷一个角标、且事件链上有 `ScrollToTheBottom` ⇒ 不能靠它刷 | EFT.UI.Chat/DialogueView.cs:310-324；EFT.UI.Chat/MessagesContainer.cs:100-103 |
| `UpdatableChatDialogue` 复制 Message 时**不复制 HasRewards** ⇒ 没点开过的会话恒 false | ChatShared/UpdatableChatDialogue.cs:146-158, 261-285 |
| `social.Dialogues` 只含**已加载**的会话；每页 30 条；玩家不滚列表就只加载 30 条 | EFT/SocialNetwork.cs:576, 842；EFT.UI.Chat/DialoguesContainer.cs:169-183 |
| ★原版 `UpdateDialogueList` 每页会自动选中第一个会话 ⇒ 清掉它的计数 | EFT/SocialNetwork.cs:865, 1466-1479；EFT.UI/MenuTaskBar.cs:769-777 |
| 服务端列表载荷自带 `@new` / `attachmentsNew`（权威值） | ChatShared/ChatRoomInformation.cs:16-18 |

## ★为什么补拉不走原版 UpdateDialogueList

原版 `SocialNetwork.UpdateDialogueList` 每页回调里执行 `CreateDialogue(array[num2], num2 == 0)`，
第二个参数就是 `select=true` ⇒ **每页第一个会话被自动选中** ⇒ `MenuTaskBar.DialogueSelectedHandler`
把它的 `New` / `AttachmentsNew` 写死为 0。

后果有两层，都很致命：

1. 计数被清零 ⇒ 那个会话既不在「本地有未读」集合里，服务端状态又没被采到
   ⇒ id 不进 `/client/mail/dialog/read` ⇒ **服务端未读保留、重登后角标复现**。
2. 玩家当前正在看的会话被切走。

所以本插件的 `DialogueLoader` **直取服务端载荷**（`POST {messaging}/client/mail/dialog/list`）：
自取 `ChatRoomInformation`，自己翻页，自己采集权威的 `@new` / `attachmentsNew`，
再用 `CreateDialogue(room, select: false)` 把会话灌进本地列表（**select=false** ⇒ 不触发自动选中、不清计数）。

★游标从 **0** 开始、按**服务端实际返回条数**推进，绝不用 `Dialogues.Count()`：
后者等于「跳过本地已加载的部分」，会让服务端前几页（恰恰是被原版清零过的那批）永远查不到。

补拉期间还会临时置 `social.MessageListUpdating = true`，避免原版 `DialoguesContainer.Update()`
同时发起它自己的分页（那条路径同样会自动选中并清零），结束后恢复原值。

★`MessageListUpdating` 只能挡住原版**发起新请求**，挡不住**已经在飞**的请求：
那个在途请求的回调里照样会 `CreateDialogue(array[0], select:true)` ⇒ 把玩家当前会话切走。
插件不主动恢复（会把玩家补拉期间自己的手动选择也顶掉），详见「已知局限」第 9 条。

## 文件结构

| 文件 | 职责 |
|---|---|
| `MailTakeAll41.csproj` | 工程定义，netstandard2.1，HintPath 指向 `G:\EFT4.1\` |
| `MailTakeAll41Plugin.cs` | BepInPlugin 入口、Harmony 挂载、挂载结果日志 |
| `Patches/ChatScreenShowPatch.cs` | 挂 `ChatScreen.Show` 后置补丁，建两个按钮 + 存活探针 |
| `Ui/ButtonFactory.cs` | 自绘 UGUI 按钮工厂 + 幂等挂载守卫（两个按钮共用） |
| `Ui/TakeAllButton.cs` | 「全部收取」按钮 |
| `Ui/MarkAllReadButton.cs` | 「全部已读」按钮 |
| `Ui/DialogueBadgeRefresher.cs` | 直接刷可见格子的两个角标（不走事件链） |
| `Ui/UiFontCache.cs` | TMP 字体三级兜底解析（静态缓存，只探测一次） |
| `Services/DialogueLoader.cs` | 会话列表补拉分页（两个流程共用，直取服务端载荷） |
| `Services/DialogueCollector.cs` | 逐会话取附件 + 按消息 id 去重 + 记录已收空会话 |
| `Services/TakeAllRunner.cs` | 「全部收取」编排：只调一次 ProcessRewards，fire-and-forget |
| `Services/MarkAllReadRunner.cs` | 「全部已读」编排：清角标 + 服务端标记 |
| `Services/OperationGate.cs` | 两个批量流程共用的重入闸 |
| `Services/PendingAttachments.cs` | 「有未领附件」的会话登记（落盘，保住收取候选判据） |

## 三个必须避开的坑

1. **绝不循环调 `ChatScreen.TransferAll(dialog)`**。第一次调用就会 `chatPanel.Close()` 并打开
   TransferItemsScreen，之后每次调用都被 `MainMenuShowOperation` 里
   `CheckCurrentScreen(EEftScreenType.TransferItems)` 守卫**静默丢弃**（不抛异常，表现为「按了没反应」）。
   正解：合并成一个数组后**只调一次** `ProcessRewards`。
2. **对话列表每页只拉 30 条**。玩家没滚过左侧列表时，未加载的会话不在 `Dialogues` 里 ⇒ 会漏收、漏标已读。
   必须补拉，且**不能走原版那条会自动选中并清零的路径**（见上一节）。
3. **`ProcessRewards` 是 async Task**，内部 await 玩家点确认窗。
   必须 fire-and-forget，**Harmony Postfix 里绝不能 await**，否则死锁或吞异常。

## 已知局限

1. **服务端是否随 read 连带清 `attachmentsNew`：未知**（客户端源码查不到）。
   本插件已用落盘的 `PendingAttachments` 兜底（「全部已读」时登记、「全部收取」确认无附件后清理），
   但若服务端清了 `attachmentsNew` 且附件仍未领，重进游戏后该附件可能既无角标、也不在收取候选里。
   验证法：全部已读 → 退出游戏重进 → 看左侧附件角标与收取结果。
2. **清零后原版不再自动补报已读**：原版 `AddDialogueToReadQueue` 门槛含 `New>0 && AttachmentsNew!=0`，
   两字段清零后该门槛不再成立 ⇒ 之后新到的消息不会被原版自动上报已读（角标会重新亮起，符合直觉）。
   本插件每次点「全部已读」都会把当前所有未读会话重新上报一次。
3. **按钮坐标未实机校准**：两个按钮挂在左侧会话列表容器底部正中（±56, 10），
   坐标未实机确认，有压住列表最后一行的风险。
4. **中文字形未实机确认**：已做三级字体兜底，但「全部收取」「全部已读」字形是否落在借到的字体资产内未验证
   （最坏情况标签空白，有 Warning 日志，不影响功能）。
5. **跨会话合并后传送界面的过期倒计时取全消息最小值**；任务物品走独立 `TransferAllQuestItems`；
   `ProfileChangeEvents`（等级/站台/技能类任务奖励）在批量路径下不被 redeem。**均为原版机制。**
6. **插件目录会多一个 `MailTakeAll41.pending-attachments.txt`**：这是「全部已读」的补偿登记文件，
   删掉它不影响功能（只是跨进程补偿失效）。
7. **全局频道不纳入已读标记**：原版 `AddDialogueToReadQueue` 明确排除 GlobalChat
   （SocialNetwork.cs:1439），本插件照此口径，其余会话一律纳入。
8. **取件失败 ≠ 已收空**：`getAllAttachments` 是 NoRetries（ClientBackendSession.cs:2468-2476），
   一次网络抖动就会失败。本插件只把「**请求成功且确实没有附件**」的会话从待收登记里清掉；
   取件失败时保留登记、计入失败数并打 Warning。
9. **原版在途请求的自动选中副作用**：`MessageListUpdating` 只能挡住原版**发起新分页**，
   挡不住**已经在飞**的请求，那个请求的回调仍会自动选中第一个会话、把玩家当前会话切走。
   **插件刻意不恢复选中状态**：补拉窗口内无法区分「被原版顶掉」与「玩家自己点了别的会话」，
   强行恢复会把玩家的手动选择也顶掉、并顺带清掉回复引用，副作用比不恢复更大。
   该副作用同时会把那个会话的本地计数清零 —— 但插件采集未读状态用的是**服务端权威值**，
   所以它的 id 仍会进已读提交集合，不影响「全部已读」的覆盖面。
10. **取件失败会静默等满 30 秒**：`getAllAttachments` 是 NoRetries，且 `ISocial` 接口返回 void，
    请求在 async 内部抛异常时回调不触发 ⇒ 整批等到取附件超时才继续（数据不丢，登记保留）。
11. **补拉会把服务端全部会话一次性灌进左侧列表**：原版是滚动到底才逐页加载，插件是一次拉完，
    所以左侧条目会一次性变多（会话内容与原版一致，只是加载时机不同）。

## 日志

- 启动只留两行 Info：版本 + 挂载结果；其余逐条明细走 LogDebug。
- Warning/Error 永不静默：收集失败、去重异常、ProcessRewards 空数组、字体缺失等都有可见日志。
- 存活探针：`[MailTakeAll][存活探针] ChatScreen.Show 后置补丁已挂上并被调用`
  （区分「挂载行」与「热路径被调用行」，首次 Info、后续 Debug）。
- 「全部已读」结果行：
  `[MailTakeAll] 全部已读完成：本地待处理会话 N，服务端补充会话 N，未领附件会话已登记 N，本地已清零 N，提交已读 id N，服务端回执 …`
- 角标一个格子都没命中时会打 Warning，便于实机对账。

## 构建

```bash
"/c/Program Files/dotnet/dotnet.exe" build src/MailTakeAll41/MailTakeAll41.csproj -v m -o _archive/MailTakeAll41_20261023
```

（PATH 里的 dotnet 是 x86 无 SDK，必须用完整路径。）

本仓库源码以 **AGPL-3.0-or-later** 发布，素材与文档以 **CC BY-NC-SA 4.0** 发布（详见 LICENSE 与 ASSETS-LICENSE）。

## 开源协议
- 源代码：AGPL-3.0-or-later。衍生作品必须以同一协议发布，并公开完整对应源码。本项目在服务器上运行时，向通过该服务器与之交互的所有用户提供完整对应源码（AGPL §13）。
- 素材与文档（图片/图标/模型/译文/说明）：CC BY-NC-SA 4.0。须署名、禁商用、衍生作品同协议发布。
- 源码获取渠道：见本仓库 Releases 与随包附带的 source/<mod>-src.zip。
