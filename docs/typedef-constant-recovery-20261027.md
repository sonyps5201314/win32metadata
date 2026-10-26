# typedef 常量投影回退修复

日期：2026-10-27。

## 问题与原因

用户比较 LASTOK 与迁移前修复基线的 `Windows.Win32.System.Threading.h`，发现 21 个原有常量定义消失，包括 FLS_OUT_OF_INDEXES、TLS_OUT_OF_INDEXES 和 19 个 PROC_THREAD_ATTRIBUTE_*。

这些常量仍存在于 winmd 的原命名空间中。恢复更多 typedef 后，字段签名从 UInt32 等基础类型变成 NativeTypedef 结构体表示的 DWORD 等类型。cppwin32 的旧 Literal 分支把所有结构体类型常量都移到全局 `EnumFromUdtConst.h`，没有在原命名空间头文件中声明。普通命名空间头文件也没有包含该文件，所以原有可见性和限定名称均发生回退。

全树比较发现同类回退涉及 45 个头文件、802 个原有 inline constexpr 常量。其中 328 个为数值常量，474 个为宽字符串。宽字符串被错误放入整数枚举，进一步导致包含 EnumFromUdtConst.h 时产生非法初始化。

此前冻结输入的两次导出完全相同，只能证明两轮处理相同输入时结果一致，未检查 LASTOK 原有常量的命名空间覆盖。本轮补齐了这一项验收。

## 修复

cppwin32 提交：90b68a1184aa2b1f0f6808b7d05fd3ea708cadc3。

- 对新算术 typedef，沿唯一实例 Value 字段解析到基础算术类型，在所属命名空间输出 `inline constexpr`，保留 typedef 类型名。
- 字符串按原来的只读宽字符指针字面量输出，保留原命名空间和值，不进入整数枚举，也不把只读字面量绑定到可写 PWSTR。
- 保留已有 HRESULT、NTSTATUS、BOOL、VARIANT_BOOL、BOOLEAN、DEVPROP_BOOLEAN、HTREEITEM、SOCKET、WSAEVENT 的历史枚举接口。
- 指针资源标识符及其他已有 UDT 枚举路径保持原有处理。

示例输出现在恢复为：

```cpp
inline constexpr DWORD FLS_OUT_OF_INDEXES = 0xffffffff;
inline constexpr DWORD TLS_OUT_OF_INDEXES = 0xffffffff;
inline constexpr DWORD PROC_THREAD_ATTRIBUTE_PARENT_PROCESS = 0x20000;
```

winmd 不需要修改或重新生成；修复发生在 C++ 头文件投影层。

## 验证结果

1. 用修复后的 cppwin32 重新导出原始 LASTOK.winmd，1296 个文件与 Z:\LASTOK 逐文件 SHA-256 完全相同。
2. 重新导出相同的旧 SDK 修复基线，802 个回退常量全部恢复到原命名空间头文件，名称和值与 LASTOK 逐项一致；Threading 中的 21 个均恢复。
3. 检查旧基线全部 88597 个 Apis 字面常量，均可在所属命名空间头文件或保留的兼容枚举中找到；其中 7387 个字符串值逐项一致。
4. 检查 SDK 28000 全部 89317 个 Apis 字面常量，均可找到；其中 7547 个字符串值逐项一致。
5. 两套 EnumFromUdtConst.h 中不再残留宽字符串字面量。
6. 新增 test/ConstantProjectionTests，实际执行 C# 输入 → winmd → cppwin32 → 原生 C++ 编译和运行。x86、x64 均通过，断言覆盖普通/嵌套算术 typedef、指针宽度整数、浮点、宽字符串、普通常量及旧枚举接口。
7. SDK 28000 仍完整导出 1308 个文件；与修复前同版本输出相比，改变 46 个文件。
8. 用相同修复后的投影器比较旧 SDK 与新 SDK，仍为 1300 → 1308 个文件，新增 8、删除 0、改变 159、相同 1141。

构建使用 Visual Studio 18 devenv.com，配置为 Debug|x64 和 Debug|x86。cppwin32 原有 7 个测试项目工作区修改逐字节补丁核对未变化。

## 产物与证据

当前 `Z:\TYPEDEF` 已同步修复后的 SDK 28000 头文件，1308 个文件与验收目录逐文件哈希相同。

修复后的独立导出：

- `Z:\winmd-constant-recovery-20261027\accepted\lastok`。
- `Z:\winmd-constant-recovery-20261027\accepted\baseline`。
- `Z:\winmd-constant-recovery-20261027\accepted\sdk28000`。

用户指出的旧历史 baseline/rebased 目录保持原状，用于保留缺陷和复现记录。当前 TYPEDEF 发布前的完整副本保存在 `Z:\winmd-constant-recovery-20261027\TYPEDEF-before-fix`。

winmd SHA-256 保持为 `1FDC1943328F8C96570F7DE61C0A17787A456EB3AB6A2365AC686D88828DF960`。

证据目录：`L:\Open_Code\win32metadata\bin\constant-recovery-20261027`。

- `lastok-header-constant-regressions.json`：45 个头文件、802 个原有常量清单。
- `missing-constant-type-groups.json`：回退常量的元数据类型分类。
- `regression-verification.json`：LASTOK 全树相同及 802 个名称/值恢复结果。
- `baseline-literal-coverage.json`、`sdk28000-literal-coverage.json`：全量字面常量及字符串值覆盖结果。
- `corrected-sdk-diff/comparison.json`：修复投影器后的 SDK 升级差异。
- `published-artifacts.json`、`headers-manifest.json`：提交、二进制哈希和实际发布位置。

cppwin32 与 win32metadata 均建立了 `backup/pre-constant-recovery-20261027` 分支。历史产物、用户改动和旧 winmd 均保留，没有推送远端。
