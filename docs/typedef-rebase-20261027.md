# typedef 指针数组修复与上游迁移验收

日期：2026-10-27。

本文件记录冻结旧输入的 rebase 回归验证。随后补齐的 SDK 28000 完整重新抓取、导出和结构体差异，见 [SDK 28000 完整生成验收](sdk-28000-regeneration-20261027.md)。

## 验收结论

当前基线已修复固定数组元素丢失指针的问题，并完成 winmd 和 cppwin32 全量头文件导出。随后将 ClangSharp 和 win32metadata 的本地功能迁移到用户提供的官方最新快照，再对同一套完整输入重新生成并导出。

两轮均导出 1300 个文件，逐文件 SHA-256 全部相同，新增、删除和内容变化均为 0。两份 winmd 长度均为 24492544 字节；二进制仅有 18 个不同字节，屏蔽模块 MVID 和 PE 时间戳后逐字节完全相同。

比较使用冻结的完整抓取输入，以隔离代码迁移与 SDK 更新的影响。该产物不是对 SDK 10.0.28000.2705 重新做全量抓取的结果。新 ClangSharp 22 抓取链另经 x86、x64、ARM64 的实际抓取及小样本端到端布局验证。

## 提交与备份

| 仓库 | 迁移前验收提交及备份指向 | 迁移后代码提交 |
| --- | --- | --- |
| cppwin32 | e8be201d7518742e4674549db106a4f6fa532483 | 相同，GUID typedef 常量修复 |
| win32metadata | e4d1cabc707e5360f9c62967ced906803f83fceb | 1c9a93f18da5ef6f4b76411f83a4f7c13603dbb8 |
| ClangSharp | 352c6d1b0e9a7341265c1fa4ebb5ffd7d61a097e | 81cbfab7179a0c4d8eb57124717507f2ddc3f7ba |

三个仓库均有独立的 `backup/pre-upstream-20261027` 分支，指向表中的迁移前提交。实际工作目录的 main 已完成迁移；cppwin32 不涉及此次上游 rebase。

上游基线：

- ClangSharp：f9fccc9c8623a11348bc326f3c6e6dfbfae376e8。
- win32metadata：2f767671802301cf043c717d66bf004ed7863487。

两个上游提交均已验证为相应当前 main 的祖先。原始本地历史保留在备份分支中。

## 当前基线修复

`GetNormalizedNativeTypeName` 和 `RemapToMoreSpecificTypeIfPossible` 增加数组元素上下文。固定缓冲区发射时先从原始 NativeTypeName 去掉数组维度，随后只映射元素类型。删除了只要符号变化就执行 PointedAtType 的旧逻辑。

这样既保留 `T*[N]`、`T**[N]` 中的真实指针，也不会给自身已经包含指针的 `PVOID[N]` 多加一层指针。该路径还避免了旧数组归一化正则破坏 `**[N]` 声明。

对原错误 winmd 的全部字段记录做比较，共有 9 个字段身份的类型签名变化：3 个为确认的指针恢复，6 个为恢复原生 typedef 名称或等价名称选择。完整差异保存在 `bin/rebase-20261027/baseline/pointer-fix-field-diff.json`。

确认恢复的三个字段：

| 字段 | 修复前 | 修复后 |
| --- | --- | --- |
| RTL_BALANCED_NODE._Anonymous1_e__Union.Children | _RTL_BALANCED_NODE[2] | _RTL_BALANCED_NODE*[2] |
| RPC_IF_ID_VECTOR.IfId | RPC_IF_ID[1] | RPC_IF_ID*[1] |
| UUID_VECTOR.Uuid | UUID[1] | UUID*[1] |

新增 `FixedBufferPointerTests` 对实际 winmd 字段签名验证一层指针、二层指针、指针 typedef 和按值数组。

GUID 常量采用用户选择的方案：cppwin32 沿 NativeTypedef/MetadataTypedef 的唯一实例 Value 字段确认底层 System.Guid 后读取 GuidAttribute，输出保留 IID、CLSID、UUID 名称。该修复已经单独提交。

## rebase 处理

- 保留本地的不完整数组、零长度数组、原始字段名和更多 typedef 恢复功能。
- NativeAnnotation 开关从旧的位 41 移到位 47，避开新上游 GenerateFixedBufferIndexerOverloads。
- 使用上游的 NativeAlignment 发射实现，删除本地重复发射逻辑。
- 保留 winmd 中 NativeAlignment(long) 的现有 schema，避免破坏 cppwin32 的 int64_t 属性解码；抑制抓取器生成另一个同名属性类，并映射到 Windows.Win32.Foundation.Metadata。
- 新 Win32MetadataScraper 改为引用同级 ClangSharp 源码项目，实际使用本地改动后的 22 版程序集；移除对已被 ClangSharp 22 删除的 ElaboratedType 的引用。
- 保留上游已更新的 TOKEN_AUDIT_POLICY=60、依赖版本占位符及自动 API diff 工作流，没有恢复旧生成式 ChangesSinceLastRelease.txt。
- 保留 NDR_SCONTEXT、SLIST_ENTRY、MONITORINFOEX 和 NONAMELESSUNION 的本地恢复意图。

新增 `LocalTools.slnx` 统一构建本地源码组合。默认 ClangSharp 源码位置为 `../ClangSharp`；Win32MetadataScraper 的 `ClangSharpSourceRoot` 可覆盖项目引用位置。

## 构建与验证

所有 C#、C++ 项目构建均使用用户指定的 Visual Studio 18 devenv.com。缺少资产文件时仅用 MSBuild 的 Restore 目标恢复包，未使用 dotnet build。

已完成：

1. 迁移前发射器测试：7 项全部通过。
2. 迁移后发射器测试：22 项全部通过，包括上游跨架构枚举、GUID 和架构引用测试。
3. ClangSharp 22 及其 CLI 全部重新生成成功。
4. 本地 Win32MetadataScraper、MetadataTasks、ConstantsScraper 构建成功。
5. 抓取器 x86、x64、ARM64 验证：原始字段名、IncompleteArray、SizeZeroConstantArray、单次 NativeAlignment、默认关闭 NativeAnnotation、一层与两层指针均通过，三架构生成源码 SHA-256 相同。
6. 小样本完整执行 `C/C++ header -> 新抓取器 -> C# -> winmd -> cppwin32`，确认输出 `T*[2]`、`T**[2]`、`Values[0]`、`Values[]` 和 `__declspec(align(16))`。
7. 小样本生成头文件在原生 x64、x86 项目中编译通过；sizeof、offsetof、alignof 静态断言与原始 C/C++ 声明一致，两个程序运行均返回 0。
8. 全量两轮 cppwin32 导出成功：1300/1300 个文件 SHA-256 相同。
9. 两轮读取的 40850 个类型记录相同；进一步屏蔽 MVID 和时间戳后，整个 winmd 二进制相同。
10. 冻结输入 829 个文件的 SHA-256 复核无变化。

对零长度数组原始声明，MSVC 给出预期的 C4200 扩展提示。部分 devenv 日志前部保留了 IDE 后台恢复项目详情的提示以及准备阶段失败尝试，最终显式恢复、构建和验证结果以上述成功记录为准。

## 产物与复现入口

当前使用的 winmd：`L:\Open_Code\win32metadata\bin\Windows.Win32.winmd`，与迁移后验收文件哈希一致。

| 文件 | SHA-256 |
| --- | --- |
| 迁移前修复基线 winmd | 17D2770B1FC41F3A3845B33BD8A9FD814A3402489DAFF6A6786E41A3F0650025 |
| 迁移后 winmd | 1828820BC6D45F98DAD200B7AC4D1E40E6C4C4CE505018B36B0FF6AAD6722F9A |

验收目录：`L:\Open_Code\win32metadata\bin\rebase-20261027`。

- `baseline/Windows.Win32.winmd`、`rebased/Windows.Win32.winmd`：两轮 winmd。
- `frozen-input`、`frozen-input-manifest.json`：完整冻结输入和哈希。
- `header-diff/comparison.json`：1300 个头文件零差异报告。
- `metadata-comparison.json`、`binary-comparison.json`、`pe-identities.json`：类型及二进制比较。
- `tests/pointer-rebased.trx`：迁移后测试结果。
- `clang-smoke`：三架构抓取、端到端生成、原生布局构建日志。
- `local-toolchain-verification.json`：抓取器实际加载的 PInvokeGenerator DLL 与同级源码构建 DLL 的哈希一致性记录。
- `Invoke-FrozenEmitter.ps1`、`Export-Headers.ps1`、`Compare-Headers.py`：本次使用的复现脚本。

两轮全量头文件目录分别为：

- `Z:\winmd-rebase-20261027\baseline`。
- `Z:\winmd-rebase-20261027\rebased`。

最终验收头文件还已同步到用户原先使用的 `Z:\TYPEDEF`，1300 个文件与迁移后验收目录逐文件哈希相同。同步前的完整目录保存在 `Z:\winmd-rebase-20261027\TYPEDEF-before-publish`，发布记录为 `bin/rebase-20261027/published-headers.json`。

原始错误文件和 LASTOK 文件仍保留在验收目录中的 original-* 文件，LASTOK 原文件也未覆盖。

## 用户原有修改

cppwin32 的 7 个测试项目文件修改没有纳入此次提交，保持原状。ClangSharp 的 GenerateClang 本机调试参数已恢复为工作区修改；空游标断言屏蔽已由上游覆盖，未再次制造相同补丁。

ClangSharp 原始两个工作区改动仍保存在 stash `9c9005dc340e7b3b285959daa744e2b7c647e740`，另有迁移前补丁文件。各仓库的 `.codegraph/` 是按协作规范初始化的本地索引，未提交。没有推送远端。
