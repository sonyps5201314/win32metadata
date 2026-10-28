# 原生类型与头文件全量差异审查（2026-10-29）

本轮对用户指定的 `Z:\win32` 和 `Z:\TYPEDEF\win32` 做完整文件比较，在同一 SDK 输入上修正生成器和 cppwin32。参考输出和修复前输出分别冻结在 `Z:\winmd-full-audit-20261028\reference`、`before`；验收候选位于 `accepted\win32`。源码起点为 win32metadata `ae01a7caaa143f8184884896710472fd5c7f581d`、cppwin32 `0934c5256f04164fd208693091c11462ad385c2d`。

## 两个报告案例

`IActiveScriptProfilerHeapEnum::GetNameIdMap` 的 SDK 原文是 `LPCWSTR *pNameList[]`，包含显式的 `*` 和形参数组退化。底层等价类型是 `wchar_t const***`。参考输出的 `PCWSTR` 丢失间接层数，修复前的 `const uint16_t***` 保留了底层指针却丢失 SDK 别名；现在输出 `PCWSTR* pNameList[]`。项目沿用 `PCWSTR` 这一与 `LPCWSTR` 等价的只读字符串名称，保留数组形式与完整指针层数。

`IWebAppDiagnosticsObjectInitialization::Initialize` 的 `hPassedHandle` 恢复为 `HANDLE_PTR`，其规范底层为 `UIntPtr`。不能因为公共抓取输入使用 32 位整数，就把跨架构句柄固定为 `uint32_t`。

## 修复范围

- 规范的 NativeTypedef/MetadataTypedef 定义优先于观察到的存储形式，恢复 HANDLE_PTR、JET_*、REGHANDLE、BOOL、PDH_STATUS 等名称及正确的平台宽度。只有明确声明的整数别名允许同宽有符号性差异；推断别名仍有严格的正负测试。
- 根据 SDK 名称和显式配置补回 73 个枚举别名。对指针、枚举、领域类型的恢复不能仅凭整数位宽猜测；需单独配置的标量成员使用限定名称映射。
- cfgmgr32 的 `PRIORITY` 是 `ULONG`，与 HtmlHelp 同名枚举无关。新增 `DeviceAndDriverInstallation.PRIORITY` 的 `uint` 原生别名，仅明确映射两个配置管理器参数；测试同时验证限定名称和底层 UInt32。
- `PWSTR/PSTR` 转只读别名时保留外层指针；形参的 `IncompleteArrayAttribute` 还原为数组语法，所有 API、COM 方法和回调使用同一参数声明输出路径。
- 固定缓冲区恢复 FLOAT/BYTE/UCHAR/WORD 等元素别名，避免 `FLOAT[3][4]` 变成 `FLOAT*[12]`。新增 `AutoType.ArraySize`，支持 DOT11_MAC_ADDRESS[6]、SpatialAudioHrtfOrientation[9]，保留外层数组及指向整个数组的指针。
- 将接口对象别名与接口指针别名分开。对象别名以 `AlsoUsableFor` 和实际接口类型共同确认，避免 ID3D10Blob、IDWriteGeometrySink 等被误写成指针 typedef；参数所需的隐式接口指针仍保留。
- 回调名称恢复需同时核对命名空间、调用约定、完整参数列表及返回值。跨命名空间的既有回调规范化继续保留，避免引入真实的命名空间依赖环。
- `LPVOID/PVOID` 优先取真正的 `void*`；不再让某个 byte 缓冲区的观察结果改变其规范定义。私有不透明结构体不伪造为整数结构体，相关成员明确使用 PVOID。
- 两个 WmFormat 接口的 `NegotiateConnection` 用精确成员映射恢复 `DMO_MEDIA_TYPE*`。其与 AM_MEDIA_TYPE 的九个字段相同，但 SDK 实际使用 DMO_MEDIA_TYPE；不能只因布局相同就接受名称变化。
- MSI 的 SDK 定义是 `INSTALLUI_HANDLER_RECORD` 为函数指针、`PINSTALLUI_HANDLER_RECORD` 为该函数指针的指针。在 Setup 抓取设置中保留这一区分，重新抓取同一 SDK 的 Setup 公共分区，并为合法的指向回调的指针增加一条精确允许项。
- cppwin32 支持枚举 typedef 的所属命名空间常量输出，保留其类型并作明确枚举转换。

## 全量比较与判定

两个初始目录均为 1,308 个文件，225 个文件存在差异，共 1,345 个文本差异块。验收输出仍为 1,308 个文件，无文件增删；相对参考有 212 个文件不同，共 3,242 个差异块。差异块变多是因为本轮进一步恢复了大量原本已经写成基础类型的数组元素和别名，不能用差异块数量单独评价正确性。

结构化声明目录覆盖 API 原型、COM 方法、回调、普通 typedef 和具名结构成员，忽略声明顺序后共记录 4,891 项变化。每项的参考、修复前、验收声明、分类理由和依据保存在 [audit-decisions.json.gz](native-projection-audit-20261029/audit-decisions.json.gz)。其中 3,524 项经递归展开别名及形参数组退化后类型树等价；692 项恢复参考名称；其余项根据 SDK 定义、平台分支、成员映射和原生断言分别判定。

本次相对参考新增的 `_t` 基础整数 API 类型回退为 **0**。这一结论针对新增差异；既有无原生名称信息的基础类型没有凭空改名。

旧参考也有错误，以下变化有意保留：

- `DBSORT` 的 SDK 定义是 DWORD，参考输出错误地增加了指针。
- `FARPROC/NEARPROC/PROC` 的 SDK 返回值是 INT_PTR，参考 INT 在 64 位平台宽度不足。
- 七个结构成员的固定数组从指针元素恢复连续数值元素，包含 D3D12_RAYTRACING_INSTANCE_DESC::Transform。
- DOT11_MAC_ADDRESS、SpatialAudioHrtfOrientation 由标量恢复固定数组，PDOT11_MAC_ADDRESS 是指向整个六字节数组的指针。
- LSA 字符串记录采用 SDK 的 LSA_UNICODE_STRING/LSA_STRING 名称；已核对两组 Length、MaximumLength、Buffer 字段分别完全相同。
- x86 的 SLIST_ENTRY 在 SDK 中 typedef 到 SINGLE_LIST_ENTRY。当前采用展开后的 SINGLE_LIST_ENTRY，避免误用 WIN64 专用的对齐节点。
- 项目原有 `PMPRADMINCONNECTIONHANGUPNOTIFICATION3::param3=RAS_CONNECTION_3*` 显式修正继续生效。本地 Mprapi.h 原文为按值，这属于保留既有项目配置，不表述为本轮与 SDK 原文相同的恢复。

目录解析器不是完整 C++ AST，匿名成员按具名所有者合并，平台变体按集合审查；它用于完整列出可解析的声明差异，不能单独证明所有 SDK 头文件可编译。常量另通过 PE 元数据独立核对，不能由文本解析数量代替。

## 验收结果

- Visual Studio 18 `devenv.com` 构建成功；所有参数使用带引号的 `Config|Platform`。VS 的项目自动还原阶段仍报告无法加载部分跨仓库项目详细信息，随后实际构建为 12 成功、0 失败，测试程序集均更新并执行。
- .NET 测试 **179/179**：49 个发射器测试、21 个 MetadataUtils 测试、50 个抓取器测试、59 个 Windows.Win32 测试。
- WinmdUtils：无重复类型、无损坏架构引用、无命名空间环。
- **158,115** 个字面常量和 **5,910** 个 GUID 全量比较：无缺失、无新增、无值变化。
- cppwin32 测试夹具在 x64、x86 均编译并运行通过：句柄宽度、枚举别名常量、原生 Boolean、字符串指针层数、固定数组范围和数组指针、既有常量及 FLOATOBJ 架构分支均通过。
- 实际 SDK 投影的六组接口对象别名身份在 x64、x86 编译通过；配置管理器 PRIORITY 的实际头文件类型身份在 x64 编译通过。
- 最终元数据为 41,403 个类型、253,811 个字段、70,790 个方法；API/方法数量不变，主要新增原生 typedef 声明。
- 最终 WinMD：24,869,888 字节，版本 71.0.57.7327，SHA-256 `C4B0B235173A4F803D86A8FAE0A31296B145D02C92DBB988E6A04D805240D519`。

扩展原生检查时还发现 `DeviceAndDriverInstallation.0.h` 整组在 x86 存在 SP_* 前置声明与架构别名顺序问题。已核对参考、修复前、验收三份输出都含相同声明顺序，因此属于既有问题。本轮没有扩展修复这一组；[SdkChecks.cpp](native-projection-audit-20261029/SdkChecks.cpp) 对该组仅做 x64 检查，六组接口别名仍在两个平台检查。完整 SDK 的逐头文件独立编译不属于本轮已经证明的验收结果。

## 产物与复核

- 发布 WinMD：`L:\Open_Code\win32metadata\bin\Windows.Win32.winmd`。
- 发布 C++ 头文件：`Z:\TYPEDEF\win32`。发布前核对目标与 before 快照的完整哈希，发布后核对全部 1,308 个文件与验收输出一致。
- 完整本地证据：`Z:\winmd-full-audit-20261028`，包含各阶段 WinMD、冻结输入、导出日志、TRX、原生构建日志和差异文件。
- 仓库内保留 [最终验收清单](native-projection-audit-20261029/final-verification.json)、[常量比较](native-projection-audit-20261029/constants-comparison.json)、[全部头文件哈希](native-projection-audit-20261029/accepted-header-hashes.json.gz)、完整声明判定和两份压缩差异。无需保留 Z 盘日志也可审阅具体变化。
- 审查脚本位于相同证据目录。`inventory.py accepted/win32` 比较的是候选内部的 `win32` 内容根，避免把外层输出目录错当作文件重命名。
- 发射器使用冻结输入，仅 Setup 公共分区以同一 SDK 重新抓取；相对原始 Setup.cs 为六行增删，其中四行是内部匿名结构名称变化。未升级 SDK，未修改 ClangSharp 源码。
- 本轮未建备份分支，未推送；cppwin32 中用户原有的七个测试工程修改未纳入本轮提交。
