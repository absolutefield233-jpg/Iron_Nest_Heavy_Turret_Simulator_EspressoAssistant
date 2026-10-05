# Espresso Assistant · 咖啡机助手

**V1.1.0 · by 4Dfish**

一个给《铁巢重炮 / Iron Nest: Heavy Turret Simulator》的 MelonLoader mod：
让游戏里的意式咖啡机**稳定出一杯满分的咖啡**，不用再跟温度盘、压力盘、时机三个东西斗智斗勇。

**实测成绩：品质 100.0（温度 100.0 / 压力 100.0 / 时机 100.0）** —— 包括**冷机第一杯**。
**冲煮过程中切出游戏再回来，它会接着把这一杯冲完**（实测切出去 94 秒回来仍是 100.0）。

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

右上角会出现一个小面板，显示阶段、温度、压力、时间、转盘位置和上一杯的成绩。

- **没按 F10 的时候，屏幕上什么都不会显示**，不挡视线。
- **一杯冲完就停，不会自动接着冲。** 想再冲一杯，收拾好杯子再按一次 F10。
- **`F10` 是唯一的按键**（F1–F3 / F6–F9 都不碰）。

---

## 四、它到底做了什么

游戏里的咖啡机是个小游戏：要把**温度盘**和**压力盘**拨到理想值，再盯着计时表在恰好的时机按下冲煮。
而这台机器的脾气不太好：

- 两个盘**没有任何刻度对应关系** —— 温度盘要拨到 47 左右才稳住 93 °C，压力盘只要 1.5 就稳住 9 bar
- 冲煮的水流会把锅炉**往下带凉**（不是加热）
- 温度盘给大一倍，温度就会一路冲过头 15 度，再花五六秒降回来

### 第一阶段 · 预热（热机约 2~3 秒，冷机第一杯约 10 秒）

1. **先把两个盘压到最低**（载入杯子时游戏会把盘归位到上限，从那里起手必然冲过头）
2. **全速烧** —— 盘顶在上限就是这台机器最快的升温速度，任何参数都追不上
3. 读数接近目标时**提前松手**交给 PID（松早了最后几度会爬得很慢，松晚了会冲过头）
4. PID 把温度和压力**正好停在理想值上**

### 第二阶段 · 冲煮

5. 用**游戏真实的鼠标点击链路**按下冲煮把手（不是偷偷改数据，游戏里把手会正常转动、正常响应）
6. 全程用 PID 闭环守住温压。**关键在于温度盘的起始位置**（实测约是读数的 0.45 倍）——
   给对了温度就全程钉在 93 °C 一动不动
7. 在理想时长按下第二次把手停冲
8. 之后把两个盘拉到最低，让压力泄掉，**下一杯预热更快**

### 它还会照顾到这些情况

| 情况 | 它的反应 |
|---|---|
| **冲煮/预热途中你切出游戏** | 两个盘停在保持位置**不空烧**，其余不碰；**切回来接着冲完** |
| **忘了放杯子或咖啡粉** | 提示缺什么，**不空冲**（游戏在缺东西时也会报"就绪"） |
| **预热途中你把杯子拿走** | 在按下冲煮把手之前停下，**不白烧一炉水** |
| **冲煮途中杯子被拿走** | 把冲煮把手复位，不让杠杆卡在"开"的位置 |
| **游戏被暂停 / 长时间后台** | 站开不干预；如果机器真的不响应了，3 秒内放弃并把盘还给你 |

**实测误差**：温度平均 0.02~0.05 °C、峰值 0.1~0.7 °C；压力平均 0.002~0.004 bar、峰值 0.03 bar。

> 以上所有数字（盘位与温度的关系、冲煮会降温、前馈比例）都是**在这台机器上实测出来的**，
> 不是从游戏源码里读的（这款游戏是 IL2CPP 打包，方法体是机器码，逻辑看不到）。

---

## 五、已知限制（请先看这里再提问）

- **冷机第一杯的预热约 10 秒。**
  机器升温速度约 11 °C/秒是固定的，从冷机烧到 93 °C 物理上就要这么久，任何 mod 都改不了。
  **连续冲第二杯起只需 2~3 秒。**

- **游戏面板上的"Perfect"评语不可靠。**
  游戏是用 `Quality` 去比四条固定分数线来评级的，而那四条线是
  `Perfect=9 / Good=7 / Acceptable=5 / Poor=3`，可 `Quality` 是 **0~100 的百分数**——
  等于"Perfect"的实际门槛只有 **9%**，所以只要不是冲废了，**几乎永远是 Perfect**。
  **这是游戏自己的资源数值，与本 mod 无关。**
  **想看真实成绩请看本 mod 面板上的"上一杯品质"。**

- **冲煮把手的外观可能停在"开"的位置。**
  如果你在冲煮途中把杯子拿走，游戏会自己结束冲煮，但把手的姿势可能不会弹回来
  （`LookAtTarget` 上没有任何字段记录杠杆位置，它的姿势由动画决定，mod 读不到也没法可靠地驱动）。
  本 mod 会在这种情况下点一次把手尝试复位；即使外观没变，**也不影响功能**——
  mod 每次开冲/停冲之后都会复查机器状态并重试，外观错位不会让冲煮失灵。

- **只支持 Windows x64 的 IL2CPP 版本。**

- 游戏大版本更新后可能需要重新适配。

---

## 六、卸载

删掉这一个文件即可，不留任何痕迹：

```
<游戏目录>\Mods\EspressoAssistant.dll
```

本 mod **从不修改游戏的任何原文件**，也**从不碰你的咖啡杯**（冲好的咖啡原地不动，你自己拿）。

---

## 七、兼容性

- 与绝大多数 MelonLoader mod 共存（已与 IronNestFCS Smart、照相卫星、雷达卫星、
  卫星开关、ATMCUnlimited、FreeCards、TightValves 同场实测无冲突）。
- **只占用 `F10` 一个按键。**
- 不修改游戏的任何存档或数据，只操作咖啡机的那两个盘和把手。

---

## 八、从源码编译

`src/` 目录里是完整 C# 源码。需要 .NET SDK 6.0 和 MelonLoader 的引用程序集
（`GameDir` 指向你自己的游戏目录即可，`EspressoAssistant.csproj` 里已经把引用都写好了）：

```bash
dotnet build -c Release -p:GameDir="X:\path\to\Iron Nest Heavy Turret Simulator"
```

产物在 `bin/Release/EspressoAssistant.dll`。

> ⚠️ 编译时注意：**不要在工程目录里放第二份 `.cs` 源码备份**（比如 `xxx.backup.cs`），
> SDK 式工程会把目录下所有 `.cs` 一起编译，导致"类型重复定义"的报错。备份请改成 `.cs.bak` 后缀。

---

## 九、声明

- 代码由 **4Dfish** 编写。
- 与本游戏开发者无关，非官方内容。
- 随意使用、修改、再发布。

---
---

# Espresso Assistant (English)

A MelonLoader mod for **Iron Nest: Heavy Turret Simulator** that pulls a **perfect** shot
from the in-game espresso machine every time, instead of being a fight with two dials and a timer.

**Measured: quality 100.0 (temperature 100.0 / pressure 100.0 / timing 100.0)** — including the
first shot from a cold machine. **If you tab out mid-shot, it picks the shot back up when you
return** (94 seconds in the background was measured, and it still finished at 100.0).

**V1.1.0 · by 4Dfish**

## Prerequisites

- Windows (x64)
- Iron Nest: Heavy Turret Simulator (Steam)
- **MelonLoader 0.7.3 or newer, IL2CPP build** — **not included**, install it yourself
- .NET 6 runtime (MelonLoader needs it; most machines already have one)

MelonLoader: <https://github.com/LavaGang/MelonLoader/releases>

## Installation

1. Install MelonLoader (installer or unzip `MelonLoader.x64.zip` into the game root),
   then **run the game once** so it generates its IL2CPP assemblies. The first launch is
   slow — that is normal.
2. Close the game.
3. Extract the `Mods` folder from this archive into the game root and merge.
4. Start the game.

You should end up with `<game>\Mods\EspressoAssistant.dll`.

Do not put the whole archive inside `Mods`, and do not run it alongside BepInEx.

## Usage

Load a coffee grounds can and a cup into the machine, then press **F10**.
A small panel appears in the top-right corner while it works. Nothing is drawn at all until you
press it. **It brews one cup and stops** — set up the next cup and press F10 again.
**F10 is the only key it uses.**

## What it does

**Warming (2–3 s warm, about 10 s from a cold machine).** Both dials are parked at the bottom
first, then opened wide until each reading is close to its mark — a dial against its stop is the
machine's own fastest rate and no gain setting beats it — then the loops park both readings on
the ideals. The dials are parked to begin with because loading a cup resets them to the top of
their travel, and starting from there guarantees an overshoot.

**Brewing.** The brew handle is pressed through the game's own click path (the handle physically
moves — no values are edited behind the machine's back), then a PID loop holds temperature and
pressure for the whole shot. The one thing that matters most is the temperature dial's starting
position: it turns out to be about 0.45 of the reading, and set to that the reading simply stays
put. Afterwards both dials are parked so the pressure bleeds off.

**It also handles the awkward cases:** tabbing out mid-shot (the dials are held, nothing else is
touched, and the shot resumes when you are back); a missing cup or grounds (it refuses rather
than brewing into nothing); the cup being taken away during a shot; and a machine that has stopped
responding (it gives up within three seconds and hands the dials back).

Typical error: temperature 0.02–0.05 °C mean, 0.1–0.7 °C peak; pressure 0.002–0.004 bar mean,
0.03 bar peak.

## Known limits

- **A cold machine needs about ten seconds to warm.** Its heating rate is roughly 11 °C/s and that
  is fixed. Subsequent shots take 2–3 seconds.
- **The game's own "Perfect" grade word is not trustworthy.** The grade compares `Quality` against
  thresholds of `Perfect=9 / Good=7 / Acceptable=5 / Poor=3`, but `Quality` runs 0–100, so the bar
  for "Perfect" is effectively 9% and almost any cup passes. That is the game's own asset data,
  nothing to do with this mod. **Read the quality number on the mod's panel instead.**
- **The brew handle's resting pose can stay "on".** Taking the cup away mid-shot makes the game end
  the brew by itself, and the lever's pose is driven by an animation with no readable state behind
  it. The mod presses the handle once when that happens; even if the pose does not change, **it does
  not affect the brewing** — the mod verifies and retries the machine state around every start and
  stop.
- Windows x64 IL2CPP only. May need updating after a major game patch.

## Uninstall

Delete `<game>\Mods\EspressoAssistant.dll`. No game files are ever modified, and your coffee is
never touched — the finished cup stays exactly where it is for you to pick up.

## Compatibility

Coexists with other MelonLoader mods (tested alongside IronNestFCS Smart, the photo and radar
satellites, a satellite toggle, ATMCUnlimited, FreeCards and TightValves). Only key `F10` is used.

## Building from source

Full C# source is in `src/`. Point `GameDir` at your own install:

```bash
dotnet build -c Release -p:GameDir="X:\path\to\Iron Nest Heavy Turret Simulator"
```

> ⚠️ Do not keep a second copy of the source inside the project folder (e.g. `Foo.backup.cs`):
> an SDK-style project compiles every `.cs` under it and you will get duplicate-type errors.

## Credits

Written by **4Dfish**. Unofficial, not affiliated with the game's developers.
Use, modify and redistribute freely.
