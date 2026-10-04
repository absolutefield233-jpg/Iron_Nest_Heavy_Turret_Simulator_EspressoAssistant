# Espresso Assistant · 咖啡机助手

**v1.0.0 · by 4Dfish**

一个给《铁巢重炮 / Iron Nest: Heavy Turret Simulator》的 MelonLoader mod：
让游戏里的意式咖啡机**稳定出一杯好咖啡**，不用再跟温度盘、压力盘、时机三个东西斗智斗勇。

---

## 一、前置要求（必须先满足，否则装了也不生效）

| 要求 | 说明 |
|---|---|
| 操作系统 | Windows（x64） |
| 游戏 | Iron Nest: Heavy Turret Simulator（Steam 版） |
| **MelonLoader 0.7.3 或更高，且必须是 IL2CPP 版** | **本 mod 不包含加载器，你必须自己先装好** |
| .NET 6 运行时 | MelonLoader 需要它；多数电脑已自带 |

**MelonLoader 下载地址**：<https://github.com/LavaGang/MelonLoader/releases>
（下载 `MelonLoader.x64.zip`，或 `MelonLoader.Installer.exe`）

> 为什么必须用 MelonLoader？因为它是个"mod 加载器"——游戏本身不认识 mod，
> 需要它当转接头。市面上还有 BepInEx 之类的加载器，**本 mod 只支持 MelonLoader**。

---

## 二、安装步骤

### 第 1 步：装 MelonLoader（如果还没装）

- 用官方安装器 `MelonLoader.Installer.exe`，选中游戏目录，点安装；**或者**
- 把 `MelonLoader.x64.zip` 直接解压到游戏根目录（不要套文件夹）

然后**启动游戏一次**，让它读取游戏代码、生成中间文件。
**第一次会比较慢（几分钟），属于正常现象，耐心等。**

### 第 2 步：关掉游戏

### 第 3 步：解压本包

把本压缩包里的 **`Mods` 文件夹解压到游戏根目录**，提示合并时选"是"。

### 第 4 步：启动游戏

装对了的话，下面这个文件应该存在：

```
<游戏目录>\Mods\EspressoAssistant.dll
```

> ⚠️ **不要把整个压缩包丢进 Mods 文件夹**，那样没用。
> ⚠️ 如果你之前用 BepInEx，请先卸掉，两个加载器混用会导致本 mod 无法加载。

---

## 三、使用方法

1. 走到咖啡机前，**先放好咖啡粉罐和杯子**（机器要能进入"就绪"状态）
2. 按 **`F10`**
3. 剩下的交给它

右上角会出现一个小面板显示实时进度。

**没按 F10 的时候，屏幕上什么都不会显示**，不会挡你的视线。

---

## 四、它到底做了什么

游戏里的咖啡机其实是个小游戏：你要把**温度盘**和**压力盘**拨到理想值、再盯着计时表在恰好的时机按下冲煮。
而机器本身还会捣乱：

- 温度会自然衰减，并且**和压力互相拉扯**
- 有噪声和随机尖峰
- **冲煮一开始，温度会猛掉十几度**
- **压力会被泵推着往上爬**

这个 mod 的做法：

1. **先预热**——把温度和压力都停在**略低于目标**的位置，等冲煮把它们抬上来
2. **用游戏真实的鼠标点击链路按下冲煮把手**——不是偷偷改数据，游戏里把手会正常转动、正常响应
3. **冲煮全程用 PID 闭环**守住温度和压力（参数是在这台机器上实测扫出来的）
4. **在理想时长按下第二次把手停冲**
5. 冲完之后把两个盘拉到最低，让压力泄掉，**下一杯预热更快**

**实测成绩：品质 97~99 分**（温度 ~98 / 压力 ~97 / 时机 ~100）

---

## 五、已知限制（请先看这里再提问）

- **压力分数到不了满分。**
  冲煮开始后的头几秒，压力由泵驱动往上冲，而那段时间把手追不上它。残留误差约 2%。
  **这是机器本身的行为，不是控制精度问题。**

- **冷机第一杯的预热约 5 秒。**
  机器升温速度是固定的（约 10 度/秒），从冷机烧到 90 度物理上就要这么久，任何 mod 都改不了。
  **连续冲第二杯起会明显变快**（压力已经在上次冲完时泄掉了）。

- **只支持 Windows x64 的 IL2CPP 版本。**

- 游戏大版本更新后可能需要重新适配。

---

## 六、卸载

删掉这一个文件即可，不留任何痕迹：

```
<游戏目录>\Mods\EspressoAssistant.dll
```

本 mod **从不修改游戏的任何原文件**。

---

## 七、兼容性

- 与绝大多数 MelonLoader mod 共存。
- **只占用 `F10` 一个按键**（F1–F3 / F7 / F8 / F9 都不碰）。
- 不修改游戏的任何存档或数据，只操作咖啡机的那两个盘和把手。

---

## 八、源码

`src/` 目录里是完整 C# 源码。
编译需要 .NET SDK 6.0，以及 MelonLoader 的引用程序集（`MelonLoader.dll`、`Il2CppInterop.Runtime.dll`、
`Il2Cppmscorlib.dll`、`Assembly-CSharp.dll`、`UnityEngine.CoreModule.dll` 等），
并把 `PlatformTarget` 设为 `x64`。

---

## 九、声明

- 代码由 **4Dfish** 编写。
- 与本游戏开发者无关，非官方内容。
- 随意使用、修改、再发布。

---
---

# Espresso Assistant (English)

A MelonLoader mod for **Iron Nest: Heavy Turret Simulator** that makes the in-game
espresso machine brew a consistently good shot, instead of a fight with two dials and a timer.

**v1.0.0 · by 4Dfish**

## Prerequisites

- Windows (x64)
- Iron Nest: Heavy Turret Simulator (Steam)
- **MelonLoader 0.7.3 or newer, IL2CPP build** — **not included**, install it yourself
- .NET 6 runtime (MelonLoader needs it; most machines already have it)

MelonLoader: <https://github.com/LavaGang/MelonLoader/releases>

## Installation

1. Install MelonLoader (installer or unzip `MelonLoader.x64.zip` into the game root),
   then **run the game once** so it generates its IL2CPP assemblies. The first launch is
   slow — that is normal.
2. Close the game.
3. Extract the `Mods` folder from this archive into the game root and merge.
4. Start the game.

You should end up with:

```
<game>\Mods\EspressoAssistant.dll
```

Do not put the whole archive inside `Mods`, and do not run it alongside BepInEx.

## Usage

Load a coffee grounds can and a cup into the machine, then press **F10**.
A small panel appears in the top-right corner while it works. Nothing is drawn at all
until you press it.

## What it does

Preheats to just below the targets, clicks the real brew handle through the game's own
click path (the handle physically moves — no values are edited behind the machine's back),
holds temperature and pressure with a PID loop whose gains were measured on the machine
itself, and presses the handle again at the ideal brew time. Afterwards it parks the dials
so the next shot preheats faster.

Typical result: **quality 97–99**.

## Known limits

- **Pressure cannot score full marks.** For the first few seconds of a shot the pump drives
  pressure up faster than the handle can pull it down — about 2% residual. That is the
  machine's own behaviour.
- **A cold machine needs about five seconds to preheat.** Its heating rate is fixed
  (~10 degrees/second). Subsequent shots are much quicker.
- Windows x64 IL2CPP only. May need updating after a major game patch.

## Uninstall

Delete `<game>\Mods\EspressoAssistant.dll`. No game files are ever modified.

## Source

Full C# source is in `src/`. Build with .NET SDK 6.0 against MelonLoader's reference
assemblies, `PlatformTarget = x64`.

## Credits

Written by **4Dfish**. Unofficial, not affiliated with the game's developers.
Use, modify and redistribute freely.
