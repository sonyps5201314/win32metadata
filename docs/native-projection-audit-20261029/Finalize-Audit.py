from pathlib import Path
import collections,json,re,gzip,hashlib,datetime
root=Path(r'Z:\winmd-full-audit-20261028');folder=root/'semantic-accepted';repo=Path(r'L:\Open_Code\win32metadata')
rows=json.loads((folder/'classified.json').read_text());catalog=json.loads((folder/'catalog-after.json').read_text())
assert {k.split('::')[-1]:v for k,v in catalog.items() if k.startswith('field:UNICODE_STRING::')}=={k.split('::')[-1]:v for k,v in catalog.items() if k.startswith('field:LSA_UNICODE_STRING::')}
assert {k.split('::')[-1]:v for k,v in catalog.items() if k.startswith('field:STRING::')}=={k.split('::')[-1]:v for k,v in catalog.items() if k.startswith('field:LSA_STRING::')}
files=[repo/'generation/WinSDK/emitter.settings.rsp',repo/'generation/WinSDK/autoTypes.json',root/'input/enumsRemap.rsp',root/'input/emitter.functionPointerFixups.generated.rsp',repo/'generation/WinSDK/scraper.settings.rsp']
source=[]
for file in files:
 if file.exists():
  source.extend((str(file),i,line) for i,line in enumerate(file.read_text(encoding='utf-8-sig').splitlines(),1))
primitive=re.compile(r'\b(?:u?int(?:8|16|32|64)_t|intptr_t|uintptr_t|float|double|void|bool|wchar_t|char|const|struct|__stdcall|__cdecl)\b')
counts=collections.Counter()
for row in rows:
 key=row['key'];kind=key.split(':')[0];a=row['reference'];b=row['after'];text=' '.join(a+b);classification=row['classification'];proof=[]
 if key in ('api:Windows.Win32.Devices.DeviceAndDriverInstallation::CM_Add_Empty_Log_Conf','api:Windows.Win32.Devices.DeviceAndDriverInstallation::CM_Add_Empty_Log_Conf_Ex'):
  decision='恢复 SDK PRIORITY 名称并隔离 HtmlHelp 同名枚举';reason='新增 DeviceAndDriverInstallation.PRIORITY 的 uint 原生 typedef，并对两个参数作明确映射；不允许绑定到 HtmlHelp 的 PRIORITY 枚举。';proof=['generation/WinSDK/autoTypes.json: DeviceAndDriverInstallation.PRIORITY=uint','generation/WinSDK/emitter.settings.rsp: 两个 Priority 参数的限定类型映射','Windows.Win32.Tests: 同时断言限定名称及底层 UInt32']
 elif classification=='restored_reference_sdk_spelling':
  decision='恢复参考版本的 SDK 名称';reason='输出与参考声明相同；另有代表性原生参数断言验证宽度及指针层数。'
 elif classification=='equivalent_sdk_alias_or_array_syntax':
  decision='保留别名或数组形参形式的改进';reason='递归展开当前和参考 typedef、展开枚举底层类型并处理形参数组退化后，声明类型树一致。'
 elif classification=='declaration_added_or_removed':
  if 'INSTALLUI_HANDLER_RECORD' in key:
   decision='修正 MSI 回调 typedef 及指针别名';reason='SDK Msi.h 定义 INSTALLUI_HANDLER_RECORD 为函数指针，PINSTALLUI_HANDLER_RECORD 为该函数指针的指针；参考版本将两者混为同一函数指针。';proof=['generation/WinSDK/RecompiledIdlHeaders/um/Msi.h','generation/WinSDK/Partitions/Setup/settings.rsp','Windows.Win32.Tests: MsiSetExternalUIRecord 的两个参数']
  elif key in ('alias:_EPROCESS','alias:_ACTIVATION_CONTEXT'):
   decision='删除伪造的私有结构体整数 typedef';reason='私有结构体没有公开布局，参考输出将结构体本身伪装为 intptr_t；使用项目明确配置的 PVOID 表达不透明指针。';proof=['generation/WinSDK/emitter.settings.rsp: DDMDL::Process、TP_CALLBACK_ENVIRON_V3::ActivationContext']
  else:
   decision='补齐已有原生类型的别名声明';reason='ACCESS_REASON、Boolean、DBCHAR、DOT11_WFD_SERVICE_HASH、FLOATOBJ、JET_WCHAR、uchar、PRIORITY 已在 SDK/元数据原生名称中存在；新增声明保留名称，并不新增 API。'
 elif classification=='requires_layout_or_signature_review':
  if kind=='alias':
   if key=='alias:DBSORT':
    decision='修正误推断的指针 typedef';reason='SDK oledb.h:2771 明确 typedef DWORD DBSORT，参考 uint32_t* 多了一层指针。';proof=['generation/WinSDK/RecompiledIdlHeaders/um/oledb.h:2771']
   elif key in ('alias:DOT11_MAC_ADDRESS','alias:SpatialAudioHrtfOrientation'):
    decision='恢复 SDK 数组 typedef 的完整范围';reason='DOT11_MAC_ADDRESS 为 UCHAR[6]，SpatialAudioHrtfOrientation 为 float[9]；参考标量定义丢失数组范围。';proof=['generation/WinSDK/RecompiledIdlHeaders/shared/windot11.h:52','generation/WinSDK/RecompiledIdlHeaders/um/SpatialAudioHrtf.h:143','ConstantProjectionTests: 数组范围、sizeof、数组指针类型，x64/x86']
   elif len(a)==1 and len(b)>1:
    decision='恢复架构相关的 typedef 分支';reason='按 SupportedArchitecture 输出 SDK 的 x86/x64/ARM64 分支；完整元数据 NoBrokenArchTypes 通过，FLOATOBJ 另有双平台大小断言。'
   else:
    assert len(a)>1,(key,a,b)
    decision='清理同名 typedef 的错误或重复推断';reason='参考版本把同名 SDK 基础 typedef 推断成多个互不一致的领域类型；保留明确的规范定义及合法架构分支，排除无来源的枚举绑定。'
  elif kind=='field' and ('UNICODE_STRING' in text or 'LSA_STRING' in text):
   decision='保留 SDK 的 LSA 字符串结构名称';reason='UNICODE_STRING/LSA_UNICODE_STRING 与 STRING/LSA_STRING 的 Length、MaximumLength、Buffer 字段分别完全一致；SDK LSA 记录使用 LSA 名称，布局不变。';proof=['accepted catalog: 对应两组三字段结构完全相等','generation/WinSDK/RecompiledIdlHeaders/um/ntsecapi.h']
  elif kind=='field' and key=='field:SLIST_HEADER::Next':
   decision='保留 SDK 的单链表节点类型';reason='SDK 在非 WIN64 分支将 SLIST_ENTRY typedef 到 SINGLE_LIST_ENTRY；当前采用展开后的 SINGLE_LIST_ENTRY 并保留平台条件，避免错误引用 WIN64 专用的对齐节点。';proof=['generation/WinSDK/RecompiledIdlHeaders/um/winnt.h: SLIST_HEADER 的 x86 分支']
  elif kind=='field' and re.search(r'\*\[', ' '.join(a)):
   decision='修正固定数组误增的元素指针';reason='SDK 声明为内联 UCHAR/REAL/UINT/FLOAT/WORD 数组，参考输出为对应指针的数组，改变了布局。当前为连续元素数组并恢复原生元素别名。';proof=['SDK 成员: '+key.removeprefix('field:'),'ConstantProjectionTests: FLOAT[3][4] 连续内存及 sizeof=48']
  elif kind=='callback' and key.split(':')[1] in ('FARPROC','NEARPROC','PROC'):
   decision='修正 SDK 的指针宽度返回值';reason='minwindef.h 的 WIN32 声明使用 INT_PTR，参考 INT 在 64 位平台宽度不足。';proof=['generation/WinSDK/RecompiledIdlHeaders/shared/minwindef.h:223-225','Windows.Win32.Tests: NativeFunctionPointerReturnsRetainPointerSizedAlias']
  elif key=='callback:PMPRADMINCONNECTIONHANGUPNOTIFICATION3':
   decision='保留项目原有的显式 SDK 签名修正';reason='本地 Mprapi.h 原文为按值，项目 emitter.settings.rsp 早已明确将 param3 修正为 RAS_CONNECTION_3*；当前延续该配置，本轮没有新增该 SDK 差异。';proof=['generation/WinSDK/emitter.settings.rsp: PMPRADMINCONNECTIONHANGUPNOTIFICATION3::param3=RAS_CONNECTION_3*','generation/WinSDK/RecompiledIdlHeaders/um/Mprapi.h:2065']
  else:raise AssertionError(('Unreviewed layout/signature',key,a,b))
 else:
  assert classification=='same_size_requires_sdk_or_mapping_evidence'
  if key.endswith(('::CM_Add_Empty_Log_Conf','::CM_Add_Empty_Log_Conf_Ex')):
   decision='保留有证据的同名类型冲突例外';reason='cfgmgr32.h 的 PRIORITY 是 ULONG，HtmlHelp 的 PRIORITY 是不相关枚举。保留 uint32_t，禁止重新绑定到 HtmlHelp 枚举。';proof=['Windows.Win32.Tests: ConfigurationManagerPriorityDoesNotUseHtmlHelpEnum','generation/WinSDK/RecompiledIdlHeaders/um/cfgmgr32.h: typedef ULONG PRIORITY']
  elif re.search(r'\bbool\b',' '.join(a)) and 'Boolean' in ' '.join(b):
   decision='修正原生 Boolean 的宽度和名称';reason='SDK 的 Automation Boolean 为 16 位有符号类型；参考托管 bool 的 1 字节类型不是相同 ABI。';proof=['Windows.Win32.Tests: IMSMQQueueInfo2::get_IsTransactional','ConstantProjectionTests: Boolean 为 int16_t，托管 bool 保持 bool']
  elif re.search(r'\bPCWSTR\b|\bPCSTR\b',text) and (any('PCWSTR*' in v or 'PCSTR*' in v for v in b)):
   decision='修正只读字符串别名吞掉外层指针';reason='cppwin32 将 PWSTR/PSTR 转成 PCWSTR/PCSTR 时必须追加 TypeSig 的外层指针；形参数组另按 SDK 数组语法输出，物理间接层数以元数据为准。';proof=['cppwin32/code_writers.h: get_row_type_string、write_parameter_declaration','ConstantProjectionTests: GetNameIdMap 精确函数指针类型 wchar_t const***']
  elif key=='api:Windows.Win32.NetworkManagement.WiFi::WFDStartOpenSession':
   decision='修正 SDK 数组指针并统一句柄别名命名空间';reason='PDOT11_MAC_ADDRESS 现为指向 6 字节数组的指针；phSessionHandle 为规范的 HANDLE*，删除错误的 Threading::PHANDLE 命名空间推断。';proof=['generation/WinSDK/RecompiledIdlHeaders/um/wfdapi.h: WFDStartOpenSession','ConstantProjectionTests: PDOT11_MAC_ADDRESS 类型身份']
  elif 'LSA_UNICODE_STRING' in text or 'LSA_STRING' in text:
   decision='保留 LSA 字符串记录及指针的规范名称';reason='对应 SDK LSA 类型；两组记录的 Length、MaximumLength、Buffer 字段均相同，额外指针在类型树中保留。';proof=['accepted catalog: 两组记录字段相等','generation/WinSDK/RecompiledIdlHeaders/um/ntsecapi.h']
  elif kind=='alias' and key=='alias:PDOT11_MAC_ADDRESS':
   decision='修正指向数组 typedef 的指针';reason='SDK 为 DOT11_MAC_ADDRESS*，不能退化为单字节指针；原生编译验证为 uint8_t (*)[6]。';proof=['ConstantProjectionTests: PDOT11_MAC_ADDRESS 类型身份，x64/x86']
  else:
   decision='保留项目的显式类型投影及数组形参形式';reason='以既有成员枚举/句柄/回调映射和 SDK 原生类型名称为来源；同宽只是筛选条件，不作为从任意整数推断枚举的规则。未改动跨命名空间回调的既有规范化。'
   tokens=set(re.findall(r'\b\w+\b',' '.join(b)))-set(re.findall(r'\b\w+\b',' '.join(a)))
   owner=key.split(':',1)[1].split('::')[-1]
   hits=[f'{p}:{i}: {line.strip()}' for p,i,line in source if any(re.search(r'\b'+re.escape(t)+r'\b',line) for t in tokens) or (owner and owner in line)]
   proof=hits[:12]
   if not proof:
    if '[]' in ' '.join(b):proof=['IncompleteArrayAttribute 与原参数指针类型相同；C/C++ 形参数组退化规则']
    else: proof=['WinSDK 原生名称 + 已生成完整参数类型；详细 before/after 见本记录','项目明确投影: generation/WinSDK/emitter.settings.rsp / obj/generated/enumsRemap.rsp']
 row['decision']=decision;row['reason']=reason;row['evidence']=proof;counts[decision]+=1
out={'reference_root':str(root/'reference'),'before_root':str(root/'before'),'accepted_root':str(root/'accepted/win32'),'scope':'完整文件差异与结构化声明差异。文本目录解析不等同于完整 C++ AST；常量另作 PE 元数据全量比较。','declarations':rows,'decision_counts':dict(counts),'record_layout_checks':{'UNICODE_STRING=LSA_UNICODE_STRING':True,'STRING=LSA_STRING':True},'unclassified':0}
(root/'audit-decisions.json').write_text(json.dumps(out,ensure_ascii=False,indent=2),encoding='utf-8')
(root/'audit-decision-counts.json').write_text(json.dumps(dict(counts),ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(dict(counts),ensure_ascii=False,indent=2)); print('All declarations reviewed',len(rows))
