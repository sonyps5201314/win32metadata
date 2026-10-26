# SDK 28000 完整重新抓取与导出验收

日期：2026-10-27。

后续发现此处历史头文件导出存在 typedef 常量移位回退。当前 Z:\TYPEDEF 已发布修复版，winmd 不变；修复结果和新验收路径见 [typedef 常量投影修复](typedef-constant-recovery-20261027.md)。

## 为什么上一轮结果相同

上一轮的 `Z:\winmd-rebase-20261027\baseline` 和 `rebased` 都来自同一套冻结的旧抓取输入。它们验证的是发射器 rebase 后是否保持既有行为，没有重新读取 SDK 28000 头文件。因此 1300 个头文件零差异不能作为 SDK 28000 完整更新的验收结论。

本轮补齐了 SDK 更新的完整生成链。原来的冻结输入对比保留为历史回归证据，不再把它描述成新版 SDK 全量产物。

## 输入与执行范围

- 当前代码基线：637e0907667dd2feec47504f60b7dddca705e717。
- SDK 更新提交：c16762d41f1bd641ff8c4389ebf16ee50747129f。
- 依赖包：Microsoft.Windows.SDK.CPP 和 CPP.x64 10.0.28000.2705；包路径已由本次 Restore 生成的 NuGet props 确认。
- RecompiledIdlHeaders Git 树：491aef015cd8782cf0c0bec05d974ebc09b1d78b。
- 抓取器：本地修改后的 ClangSharp 22，Win32MetadataScraper 通过项目引用使用该程序集。
- 从不存在的全新目录 `bin/sdk-28000-20261027/obj/generated` 开始，未复用旧 generated C#。
- 321 个分区、395 个分区/架构抓取任务完成，随后重新提取常量和枚举。
- 本次生成阶段只运行 WinSDK .proj 的 ScrapeConstants 和 EmitWinmd 目标；C# 工具项目沿用此前用 devenv.com 构建的程序集，没有使用 dotnet build 编译工具项目。

完整链路为：SDK 28000 头文件、当前手工修正和设置 → 新抓取器 → 新生成 C#、常量和枚举 → C# 编译检查 → winmd → cppwin32 全量头文件。

## 结果

| 项目 | 旧 SDK 修复基线 | 新 SDK 完整生成 |
| --- | ---: | ---: |
| 头文件目录文件数 | 1300 | 1308 |
| TypeDef 行数 | 40850 | 41635 |
| Field 行数 | 249489 | 254043 |
| MethodDef 行数 | 69824 | 70790 |
| winmd 字节数 | 24492544 | 24875520 |

头文件逐文件比较：新增 8，删除 0，内容改变 159，保持相同 1141。

新增文件来自 DeliveryOptimization 和 WebAuthn 两个命名空间的公开头文件及各自的 .0/.1/.2 实现头文件。完整文件列表和逐文件 unified diff 位于 `bin/sdk-28000-20261027/header-diff`。

按完整类型名称比较，新增名称 1146、消失名称 385、既有类型记录改变 601。这些名称含匿名嵌套类型和 typedef 包装，不能把名称消失数量直接解释为删除了同样数量的 SDK API。MethodDef 也包含构造函数等元数据方法，不能将表行增量直接解释为公开 API 数量。

本轮同时引入新 SDK、上游抓取器行为和 AST 自动映射变化，全部头文件差异不能一概归因于 SDK 字段修改。

## 确认的结构体变化

实际元数据和生成头文件中可以看到：

| 类型 | 旧基线 | 新结果 |
| --- | --- | --- |
| INDIRECT_DISPLAY_INFO | 无驱动版本字段 | 增加 DriverVersionMajor、DriverVersionMinor |
| LDAP | 无对应字段 | 增加 ld_anonymousmaxvalrange |
| IMAGE_LOAD_CONFIG_DIRECTORY32/64 | 末尾没有该字段 | 增加 UmaFunctionPointers |
| MEMORY_DEFECT | Version、DimmInfo、ErrType | 增加 Flags |
| SHARED_COMPUTE_UNIT_RELATIONSHIP | 无该类型 | 新增类型 |
| ARM64_TPIDR2_BLOCK | 无该类型 | 新增类型 |
| XSAVE_ARM64_SME_ZA_HEADER | 无该类型 | 新增类型 |

更完整的类型和字段差异分别位于 `type-diff.json` 和 `changed-struct-fields.json`。示例在生成的 `win32/impl/complex_structs.h` 中的行号记录于 `example-locations.json`。

原先的指针修复在新版数据中仍成立：RTL_BALANCED_NODE.Children 是 `_RTL_BALANCED_NODE*[2]`，没有退回按值节点数组。新版 cppwin32 全量导出完成，未出现该循环依赖或 GUID typedef 断言。

## 日志中的提示

全量抓取产生了六条跨分区重命名警告，涉及 in_addr、sockaddr、timeval、in6_addr、_CERT_CONTEXT、_SecPkgContext_IssuerListInfoEx。

已逐项核对：实际公开 struct/enum/interface 声明中不存在这些未重命名的 tag，规范名称定义均存在。检查器在 public 方法/委托行中搜索 ` struct ` 时，误读了参数上的 NativeTypeName 字符串。审计结果见 `remap-warning-audit.json`。没有为消除提示而添加已经存在的 include，也没有关闭检查器。

发射器另外提示 WebAuthn 三架构结果可合并，属于现有 CSSW001 优化提示。本轮全部 C# 输入通过编译检查，winmd 和头文件均成功生成。

## 产物发布与保护

已同步当前使用位置：

- `L:\Open_Code\win32metadata\bin\Windows.Win32.winmd`。
- `Z:\TYPEDEF`，1308 个文件已与新版验收目录逐文件哈希核对。

新版原始验收产物：

- winmd：`L:\Open_Code\win32metadata\bin\sdk-28000-20261027\Windows.Win32.winmd`。
- 头文件：`Z:\winmd-sdk28000-20261027\headers`。
- 旧头文件发布前副本：`Z:\winmd-sdk28000-20261027\previous-TYPEDEF`。
- 旧根目录 winmd 副本：`bin/sdk-28000-20261027/previous-current-Windows.Win32.winmd`。

新版 winmd SHA-256：`1FDC1943328F8C96570F7DE61C0A17787A456EB3AB6A2365AC686D88828DF960`。

三个仓库均另有 `backup/pre-sdk28000-20261027` 分支，保存此次完整重新抓取前的提交状态。用户已有的源码工作区修改保持原状。本轮未修改业务代码。

复现和证据位于 `bin/sdk-28000-20261027`：

- `Generate-FreshSdk.ps1`：执行完整抓取/常量和 EmitWinmd 阶段。
- `fresh-input-manifest.json`、`fresh-generated-manifest.json`：输入来源和生成文件哈希。
- `fresh-sdk-generation.log`、`EmitWinmd.log`、对应 binlog：完整日志。
- `cppwin32.log`、`headers-manifest.json`：导出与文件哈希。
- `header-diff/comparison.json`：新增、删除、改变文件清单。
- `published-artifacts.json`：实际发布路径与哈希。
