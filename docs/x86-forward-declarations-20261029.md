# x86 架构前置声明修复（2026-10-29）

修复前，实际 SDK 投影 `DeviceAndDriverInstallation.0.h` 在 x86 编译失败。第一处是 `typedef SP_ALTPLATFORM_INFO_V2* PSP_ALTPLATFORM_INFO` 使用了尚未声明的 SP_ALTPLATFORM_INFO_V2，随后多个 PSP_* 别名出现相同错误及级联重定义错误。

## 根因与修改

WinMD 中同名结构按 SupportedArchitecture 保存多个定义。读取器的普通类型解析返回第一个架构版本；cppwin32 在为 typedef 提前输出依赖时，也仅输出这一个版本的前置声明。以该案例为例，x64/ARM64 声明出现在 PSP_* 别名前，x86 声明却在别名之后，因而 x86 分支无法解析该别名。

修复位于 cppwin32 的 `writer::WriteCppTypedef`：从读取器缓存的 `types_same_name` 获取完整架构变体，各自沿用已有的架构条件输出，再声明依赖它们的别名。普通类型及未列入命名空间缓存的定义保留原路径。未取消架构条件，未修改结构布局、指针层数或 WinMD。

修复后的顺序如下：

```cpp
#if defined(_M_AMD64) || defined(_M_ARM64)
    struct SP_ALTPLATFORM_INFO_V2;
#endif
#if defined(_M_IX86)
    struct SP_ALTPLATFORM_INFO_V2;
#endif
    typedef SP_ALTPLATFORM_INFO_V2* PSP_ALTPLATFORM_INFO;
```

## 回归验证

- 新增的小型用例在旧 cppwin32 上复现 x86 失败；修复后 x64、x86 编译和运行通过。
- 用例中的同名 PACKET 在 x64 为自然对齐的 16 字节结构，在 x86 为按 1 字节对齐的 8 字节结构，核对大小、对齐、NativeTypedef、MetadataTypedef 及多层指针别名。
- 实际完整 `DeviceAndDriverInstallation.0.h` 在旧输出上重新复现 x86 编译失败；新输出中两个平台均无条件包含该头文件，并重新构建和运行通过。
- 两个平台分别核对 27 个 PSP_* 别名及 PINFCONTEXT 的精确指针类型，配置管理器 PRIORITY 的限定类型，以及此前的六组 SDK 接口对象别名身份。
- 实际 SDK 回归测试保存在 cppwin32 的 `test/SdkForwardDeclarationTests`，可执行 `Run.ps1 -Headers 'Z:\TYPEDEF'`，Headers 指向包含 win32 子目录的输出根。使用 devenv.com 的 Debug|x64、Debug|x86 配置。

完整 SDK 已从同一 WinMD 重新导出，共 1,308 个文件。18 个头文件变化，完整差异只有新增的带架构条件前置声明，没有删除或改写原声明。除了 DeviceAndDriverInstallation，同类修复也覆盖 Kernel、Debug、WinSock、Shell 等命名空间的依赖声明。

本轮只修改 C++ 声明输出，未重新生成 WinMD。`Windows.Win32.winmd` 的 SHA-256 保持为 `C4B0B235173A4F803D86A8FAE0A31296B145D02C92DBB988E6A04D805240D519`。前次 179 项 .NET 测试针对的元数据及发射器未变，本轮未重复运行该套测试。

cppwin32 修复提交：`206ec8d54610e9839b355d2b5274567eb1f35150`。

## 发布与证据

18 个更新头文件已同步至 `Z:\TYPEDEF\win32`，全部 1,308 个文件与新导出结果逐一哈希一致。原 cppwin32 的七个用户测试工程修改未动，未创建备份分支，未推送。

- [验证与发布清单](x86-forward-declarations-20261029/verification.json)
- [完整头文件比较](x86-forward-declarations-20261029/header-comparison.json)
- [压缩差异](x86-forward-declarations-20261029/header-diff.diff.gz)
- [全部头文件哈希](x86-forward-declarations-20261029/accepted-header-hashes.json.gz)
- 完整本地证据：`Z:\cppwin32-x86-forward-20261029`，包含修复前完整快照、失败复现、导出日志和原生构建日志。

该验证解除前次报告中这组 .0.h 的 x86 限制；不代表完整 SDK 的所有公开头文件均已逐个独立编译，也未执行 ARM64 原生编译。
