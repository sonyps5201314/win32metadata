# 2026-10-27 typedef 恢复的完整性回归修复

## 起因及验收范围

用户运行 DoAll.ps1 后，Windows.Win32.Tests 共26项测试中20项通过、6项失败。此前性能优化验收运行的28项仅属于发射器项目，不能代表完整的元数据测试。本轮重现并修复全部6项失败，并把相关测试项目及TestCommon加入LocalTools.slnx，以便统一通过Visual Studio构建。

本轮使用用户这次已抓取的SDK 28000生成源码独立副本，固定原winmd版本重新发射；没有重新抓取或更换SDK。

## 根因与修复

1. 两项接口签名测试仍预期UInt32/Single等展开后的类型。核对SDK声明及NativeTypeName后，更新ID2D1SvgStrokeDashArray和IDWriteFactory3中对应的UINT32及FLOAT预期，保留typedef名称。
2. typedef预扫描会把DWORD已重映射为枚举的字段再次解释为typedef声明，生成DWORD包裹CONTEXT_FLAGS等错误类型；同一名字还会根据使用位置生成多个副本。现在跳过枚举和MetadataTypedef语义覆盖，按原名及可用架构维护规范声明，避免重叠定义。
3. 对底层表示不兼容的使用位置保留原来的专用类型，不再用通用NativeTypeName强行覆盖。这移除了LPHANDLE、LPBYTE、LPUNKNOWN等错误绑定带来的命名空间依赖环。命名空间循环检查没有放宽。
4. 架构相关查询按上下文选择候选，并合并同一原生类型不同C#变体的可用架构。新typedef的范围不能超过底层原生类型的可用范围。FLOATOBJ在x86保持结构体，在x64/ARM64恢复为float别名，Hypervisor通用别名和PCONTEXT也保留正确范围。
5. 获取命名空间时不再修改传出的原始指针符号；添加typedef语法树后清理旧compilation语义缓存。回归测试验证typedef内外的一层、二层指针以及函数指针的指针。
6. RtmV2.h 575–585行确认_ENTITY_METHOD本身是函数指针，PRTM_ENTITY_EXPORT_METHOD又指向该函数指针。因此其Value字段的额外指针是SDK原义。仅在既有指针检查允许表中加入该字段的精确项，没有删指针或普遍放行typedef。
7. showBrokenArchTypes原先可能打印API参数错误后仍返回0，而且没有检查返回类型。本轮同时检查API返回类型和参数，并让这些错误影响退出码，新增负例回归防止假通过。
8. C++导出发现NativeTypedef前置声明没有架构条件，可能与另一架构的同名结构体冲突。cppwin32在该分支补入与普通结构体相同的架构条件，并扩展原生常量投影测试覆盖布局。

整数资源等字面常量继续按其NativeTypeName恢复显式类型，不把合法的整数资源到指针typedef表示当作普通结构体重映射。

## 测试与产物

全部项目使用VS18 Enterprise的devenv.com构建。最终四组测试在同一次VSTest执行中全部通过：

| 项目 | 通过数 |
|---|---:|
| ClangSharpSourceToWinmdTests | 35 |
| MetadataUtils.Tests | 17 |
| Win32MetadataScraperTests | 50 |
| Windows.Win32.Tests | 26 |
| 合计 | 128 |

C++最小回归通过x64和x86的devenv构建及运行。断言验证FLOATOBJ在x64为float、FLOATOBJ_XFORM为24字节；x86中FLOATOBJ为8字节结构体、FLOATOBJ_XFORM为48字节。没有进行ARM64原生C++编译；ARM64在本轮交叉架构元数据完整性检查范围内。

修复前后158,115个字面常量、5,910个GUID常量的名称、架构键和值完全一致，无增删或变化。类型定义由41,635降到41,100，字段由254,043降到253,508，方法数仍为70,790；主要移除535个错误或冗余的typedef包装。

最终winmd同步到 `L:\Open_Code\win32metadata\bin\Windows.Win32.winmd`，大小24,838,656字节，SHA-256：

`8912091C00112E08A3BD1989D55E76DDDD045C70A43BBD9445E35C35AE2B094F`

C++完整导出前后各1,308个文件，无新增或删除。最终输出已同步到 `Z:\TYPEDEF`，逐文件SHA-256与最终导出匹配。差异清单见本目录旁 `integrity-repair-20261027/header-comparison-final.json`。

## 提交、备份及复现证据

- win32metadata功能修复：`4fbbac431a24d4cc16c361c6a10e5301b68ed967`。
- cppwin32架构条件修复：`ae2a90125c128219c2891720e7d793f75b3ff620`。
- win32metadata修复前分支：`backup/pre-integrity-repair-20261027`。
- cppwin32修复前分支：`backup/pre-architecture-typedef-20261027`。
- 完整日志、TRX、输入清单及winmd：`bin/integrity-repair-20261027`。
- C++导出前后：`Z:\winmd-integrity-repair-20261027\before`、`after`。
- 原Z:\TYPEDEF备份：`Z:\winmd-integrity-repair-20261027\TYPEDEF-before-repair`。

cppwin32中用户已有的七个测试文件修改未纳入提交，且前后git diff --binary逐字节一致；ClangSharp的既有launchSettings修改保留。
