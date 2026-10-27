# 2026-10-27 Shell typedef 回退修复

## 问题与原因

用户对比 `Z:\win32\Windows.Win32.UI.Shell.h` 与 `Z:\TYPEDEF\win32\Windows.Win32.UI.Shell.h`，发现完整性测试修复后，合法 typedef 名称被展开。上一轮全部测试通过，只能证明当时检查的完整性约束成立，未能覆盖这些 API 的原生别名。

具体原因：

- `GetTypedefPriority` 把无法从名字猜出底层类型的指针别名直接排除，影响 LPCITEMIDLIST、LPCQITAB、LPBC 等名称。现在名字匹配仅决定候选优先级，实际采用仍由底层类型及架构兼容检查决定。
- PSTR 的规范定义是 byte*，ClangSharp 把 C char* 输出为 sbyte*。新增规则仅针对 Windows.Win32.Foundation.PSTR，未放宽普通整数别名的符号检查。
- 不透明注册句柄在抓取源码中表示为 IntPtr*，规范 NativeTypedef 的 Value 是 IntPtr。兼容检查现在识别这一投影，保留 PAPPSTATE_REGISTRATION* 中调用者额外的输出指针。
- 名称相符的既有枚举可以接受底层整数类型完全匹配的输入，因此恢复 SFBS_FLAGS。枚举指针也可以恢复原生指针别名，同时保留 DWORD* 已投影为 DROPEFFECT* 的专用枚举语义。

已去掉 dependTypedefStructsCode.cs 的磁盘导出。动态 typedef 源码直接在内存解析，诊断使用虚拟文件名 GeneratedNativeTypedefs.cs。完整发射及 C++ fixture 运行后，原调试文件的时间戳和 SHA-256 均未变化。

## 验证

新增的四项最小场景测试在修复前全部失败，修复后通过；另加一项枚举指针及语义投影测试。针对完整 SDK WinMD 的九项 Shell API 签名测试，在修复前的实际产物上全部失败，修复后全部通过。

项目通过 VS18 Enterprise 的 devenv.com，配置 `Release|Any CPU` 构建。最终同一次 VSTest 运行：

| 项目 | 通过数 |
|---|---:|
| ClangSharpSourceToWinmdTests | 40 |
| MetadataUtils.Tests | 17 |
| Win32MetadataScraperTests | 50 |
| Windows.Win32.Tests | 35 |
| 合计 | 142 |

cppwin32 的 ConstantProjectionTests 在 Debug|x64、Debug|x86 下构建及运行通过，覆盖常量投影、整数资源以及 FLOATOBJ / FLOATOBJ_XFORM 的架构布局。

158,115 个字面常量与 5,910 个 GUID 常量，按所有者、字段名、类型和字段架构属性及值比较，无增删或变化。类型与字段各增加225个，分别为41,325和253,733；方法仍为70,790。

## 头文件对照范围

本轮修复前后各1,308个文件，新增0、删除0、修改208、相同1,100。按 C++ typedef 名称去重统计，恢复224个名称，没有删除本轮修复前已有的名称。

Shell 主头文件与用户的 `Z:\win32` 参考版本相比，只剩一行差异：SHDoDragDrop 的 pdwEffect 是 DROPEFFECT*。`generation/WinSDK/obj/generated/enumsRemap.rsp` 对该参数明确配置了 DROPEFFECT 映射，因此保留完整性修复后的专用枚举类型。其余字符串、PIDL、LPBC、LPCQITAB、注册句柄和 SFBS_FLAGS 的别名回退均已消除。

全树与旧参考版本仍有73个枚举包装名称差异，清单记录在 `alias-regression-20261027/typedef-comparison.json` 的 reference_aliases_still_absent。它们涉及枚举 typedef 和成员语义重映射；本轮没有恢复对所有枚举输入无条件建立包装的旧规则，也未对这些73个名称逐一完成 SDK 声明审计。因此本轮结论限于上述 Shell 签名及同类指针别名修复，不能解释为与旧参考版本全树等价。

## 发布产物与证据

使用当前 SDK 生成输入的独立副本发射，固定修复前程序集版本 `71.0.57.7327`，开启 `--validateGeneratedTypedefs`。没有重新抓取 SDK。当前机器上的修复前 DoAll 产物 SHA-256 为 `27E688C927E2F07B2353399917764FDF03A44D279508EEBEA9AF3152FA5E896F`。

最终 WinMD：`L:\Open_Code\win32metadata\bin\Windows.Win32.winmd`，24,859,136字节，SHA-256：

`15A2F8775E09E931C8B34024CDD2697E43DF0E061915832C8ACF419863AD7930`

最终头文件：`Z:\TYPEDEF`，1,308文件逐个 SHA-256 与验收输出一致。

完整证据：`Z:\winmd-alias-regression-20261027`，包含输入副本及清单、修复前后 WinMD 和头文件、构建日志、修复前失败 TRX、最终142项通过 TRX、常量比较、Shell 逐行差异和发布哈希。小型验收报告同时保存在本仓库 `docs/alias-regression-20261027`，避免 DoAll 清理 bin 后丢失结论。

依据用户本轮补充要求，没有保留或新增本轮备份分支。cppwin32 和 ClangSharp 的既有用户修改保留。
