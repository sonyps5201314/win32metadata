# 2026-10-28 Boolean 及 IShellImageData 类型投影回退修复

## 实际问题

用户指出 IMSMQQueueInfo2::get_IsTransactional 的 pisTransactional 已恢复为16位指针，但显示为 int16_t*，仍没有保留 SDK 的 Boolean*；IShellImageData 的五个方法也把原生类型展开成了 uint32_t 或 uint8_t 指针。

本轮使用与修复前一致的 SDK 抓取源码副本、程序集版本71.0.57.7327，开启 --validateGeneratedTypedefs 重新发射，没有重新抓取或升级 SDK。

## 根因与修复

### Boolean 与框架类型重名

`generation/WinSDK/RecompiledIdlHeaders/um/MqOaI.h:558` 明确定义 `typedef short Boolean`。原生名称查找优先命中 System.Boolean，导致16位原生别名没有生成；上一轮兼容检查阻止把 short* 错换成 bool*，因此虽然宽度正确，名称仍然丢失。

现在先查本次编译中的原生声明，再回退查框架类型。发现与框架类型同名且底层表示不兼容的原生别名时，生成独立的 Windows.Win32.Foundation.Boolean，避免在 System 命名空间覆盖框架类型。C++ 输出为 `typedef int16_t Boolean`，IMSMQQueueInfo 系列的 IsTransactional、IsWorldReadable 及同类参数保留 Boolean*；普通 C# bool 仍编码为框架 Boolean。

### SDK 的条件占位声明

shimgdata.h 109–124行在未包含 GDI+ 定义时，把 PixelFormat、InterpolationMode 临时定义为 DWORD，把 EncoderParameters、Image 临时定义为 BYTE。抓取后的 C# 因而使用 uint、byte*，NativeTypeName 仍记录原生名称。上一轮兼容检查拒绝了这些占位表示。

现在在有明确原生名称的指针目标位置恢复已有结构体或 typedef，并支持同宽有符号/无符号整数指针目标。规则保留外层指针数量，不把普通标量 byte 当成结构体，也不把专用缓冲区替换为无关的通用别名。

Scale 的 hints 通过 emitter.settings.rsp 中对实例方法和 vtable 委托的两项明确成员映射恢复为 Windows.Win32.Graphics.GdiPlus.InterpolationMode。为可靠使用限定类型名，EncodeHelpers.DecodeRemap 同时修复了旧解析器只保留最后一段名称的问题，支持带命名空间和 global:: 前缀的类型，并保留指针、属性及参数重命名。

最终 IShellImageData 的五处签名为：

```cpp
HRESULT GetPixelFormat(PixelFormat* pFormat);
HRESULT Scale(ULONG cx, ULONG cy, InterpolationMode hints);
HRESULT GetEncoderParams(GUID* pguidFmt, EncoderParameters** ppEncParams);
HRESULT CloneFrame(Image** ppImg);
HRESULT ReplaceFrame(Image* pImg);
```

### 保留严格的标量枚举边界

试验中曾允许所有同宽整数与枚举互换。虽然该候选通过154项测试，完整头文件审查发现它把 cfgmgr32.h 的 `typedef ULONG PRIORITY` 误套成 HtmlHelp 的同名枚举。该候选未发布到 Z:\TYPEDEF，WinMD 主产物也在审查后恢复，最终只发布收紧后的版本。

最终保留原有的标量枚举兼容约束，SDK 特例通过成员映射明确指定，并新增两项配置管理器 PRIORITY 负例。相关拒绝候选的 WinMD、差异及日志保留于证据目录的 rejected-same-width-enum，不能将其中154项通过记录当作最终验收。

## 最终验收

初始新增的三项最小场景及九项完整 SDK 接口参数测试，在修复前的实际工具及产物上全部失败。最终四个程序集在同一次 VSTest 执行中160项全部通过：

| 测试程序集 | 通过数 |
|---|---:|
| ClangSharpSourceToWinmdTests | 43 |
| MetadataUtils.Tests | 21 |
| Win32MetadataScraperTests | 50 |
| Windows.Win32.Tests | 46 |
| 合计 | 160 |

所有 C#/C++ 项目使用 VS18 Enterprise 的 devenv.com 构建。cppwin32 的最小投影回归在 x64、x86 下构建及运行通过，断言包括 Boolean 为2字节且等价于 int16_t，托管 bool 保持 bool，以及 PixelFormat*、EncoderParameters**、Image**、Image* 的类型和指针层数。没有进行 ARM64 原生编译；交叉架构元数据检查包含 ARM64。

158,115个字面常量及5,910个 GUID 常量，按所有者、字段名、架构属性及值比较无增删或变化。类型及字段各增加1个，分别为41,326和253,734；方法仍为70,790。

完整 C++ 导出前后各1,308文件，仅修改3个、其余1,305个哈希相同，没有新增或删除文件：

- win32/impl/Windows.Win32.Foundation.0.h
- win32/impl/complex_interfaces.h
- win32/impl/complex_structs.h

PRIORITY 所在的配置管理器头文件逐字节不变，上一轮 Shell 主头文件也未变化。此前记录的73个枚举包装名称差异不在本轮审计范围，本轮没有据此声称与旧参考版本全树等价。

## 发布、提交与证据

当前 WinMD：`L:\Open_Code\win32metadata\bin\Windows.Win32.winmd`，24,859,648字节，SHA-256：

`5CCCE289E2774C5E723EC4F692F55556470F6D2FAD53EE16C288B41B520A64AC`

头文件已同步到 `Z:\TYPEDEF`，全树哈希与最终验收输出一致。完整证据在 `Z:\winmd-interface-projection-20261028`，包含固定输入、修复前后 WinMD/头文件、构建日志、最终 tests-accepted/all-accepted.trx、常量比较、接口签名差异、拒绝候选和发布验证。

关键小型报告保存在 `docs/interface-projection-20261028`，避免临时目录清理后失去验收结论。cppwin32 仅新增回归覆盖，提交为 `0934c5256f04164fd208693091c11462ad385c2d`。七个既有 cppwin32 用户修改文件及 ClangSharp 的 launchSettings 修改均保留，未建立新的备份分支。
