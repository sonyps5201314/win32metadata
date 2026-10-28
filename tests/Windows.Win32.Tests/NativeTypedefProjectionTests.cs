using System.Linq;
using MetadataUtils;
using Xunit;

namespace Windows.Win32.Tests
{
    public class NativeTypedefProjectionTests
    {
        [Theory]
        [InlineData("GetProfilesDirectoryA", "lpProfileDir", "PSTR")]
        [InlineData("DeleteProfileA", "lpSidString", "PSTR")]
        [InlineData("SHCreateItemFromIDList", "pidl", "LPCITEMIDLIST")]
        [InlineData("SHCreateShellItemArray", "ppidl", "LPCITEMIDLIST*")]
        [InlineData("HlinkNavigate", "pbc", "LPBC")]
        [InlineData("QISearch", "pqit", "LPCQITAB")]
        [InlineData("UnregisterAppStateChangeNotification", "Registration", "PAPPSTATE_REGISTRATION")]
        [InlineData("RegisterAppStateChangeNotification", "Registration", "PAPPSTATE_REGISTRATION*")]
        [InlineData("StrFormatByteSizeEx", "flags", "SFBS_FLAGS")]
        public void ShellApisKeepNativeTypedefs(string methodName, string parameterName, string expectedType)
        {
            var typeSystem = DecompilerUtils.LoadDecompilerTypeSystem(TestCommon.TestUtils.Win32WinmdPath);
            var apis = DecompilerUtils.GetSelfDefinedWinmdToplevelTypes(typeSystem)
                .Single(type => type.FullName == "Windows.Win32.UI.Shell.Apis");
            var method = apis.Methods.Single(method => method.Name == methodName);
            var parameter = method.Parameters.Single(parameter => parameter.Name == parameterName);

            Assert.Equal(expectedType, parameter.Type.Name);
        }

        [Theory]
        [InlineData("IMSMQQueueInfo", "get_IsTransactional", "pisTransactional", "Boolean*")]
        [InlineData("IMSMQQueueInfo2", "get_IsTransactional", "pisTransactional", "Boolean*")]
        [InlineData("IMSMQQueueInfo3", "get_IsTransactional", "pisTransactional", "Boolean*")]
        [InlineData("IMSMQQueueInfo4", "get_IsTransactional", "pisTransactional", "Boolean*")]
        [InlineData("IShellImageData", "GetPixelFormat", "pFormat", "PixelFormat*")]
        [InlineData("IShellImageData", "Scale", "hints", "InterpolationMode")]
        [InlineData("IShellImageData", "GetEncoderParams", "ppEncParams", "EncoderParameters**")]
        [InlineData("IShellImageData", "CloneFrame", "ppImg", "Image**")]
        [InlineData("IShellImageData", "ReplaceFrame", "pImg", "Image*")]
        [InlineData("IActiveScriptProfilerHeapEnum", "GetNameIdMap", "pNameList", "PWSTR**")]
        [InlineData("IWebAppDiagnosticsObjectInitialization", "Initialize", "hPassedHandle", "HANDLE_PTR")]
        [InlineData("IWMCodecAMVideoAccelerator", "NegotiateConnection", "pMediaType", "DMO_MEDIA_TYPE*")]
        [InlineData("IWMCodecVideoAccelerator", "NegotiateConnection", "pMediaType", "DMO_MEDIA_TYPE*")]
        public void InterfacesKeepNativeParameterTypes(string interfaceName, string methodName, string parameterName, string expectedType)
        {
            var typeSystem = DecompilerUtils.LoadDecompilerTypeSystem(TestCommon.TestUtils.Win32WinmdPath);
            var type = DecompilerUtils.GetSelfDefinedWinmdToplevelTypes(typeSystem)
                .Single(type => type.Name == interfaceName);
            var method = type.Methods.Single(method => method.Name == methodName);
            var parameter = method.Parameters.Single(parameter => parameter.Name == parameterName);

            Assert.Equal(expectedType, parameter.Type.Name);
        }

        [Theory]
        [InlineData("CM_Add_Empty_Log_Conf")]
        [InlineData("CM_Add_Empty_Log_Conf_Ex")]
        public void ConfigurationManagerPriorityDoesNotUseHtmlHelpEnum(string methodName)
        {
            var typeSystem = DecompilerUtils.LoadDecompilerTypeSystem(TestCommon.TestUtils.Win32WinmdPath);
            var type = DecompilerUtils.GetSelfDefinedWinmdToplevelTypes(typeSystem)
                .Single(type => type.FullName == "Windows.Win32.Devices.DeviceAndDriverInstallation.Apis");
            var method = type.Methods.Single(method => method.Name == methodName);

            var priority = method.Parameters.Single(parameter => parameter.Name == "Priority").Type;
            Assert.Equal("Windows.Win32.Devices.DeviceAndDriverInstallation.PRIORITY", priority.FullName);
            Assert.Equal("UInt32", priority.GetDefinition().Fields.Single(field => field.Name == "Value").Type.Name);
        }

        [Theory]
        [InlineData("Windows.Win32.Storage.Jet", "JetOpenTableA", "sesid", "JET_SESID")]
        [InlineData("Windows.Win32.System.Hypervisor", "WHvCreatePartition", "Partition", "WHV_PARTITION_HANDLE*")]
        [InlineData("Windows.Win32.Devices.DeviceAndDriverInstallation", "SetupDiDestroyDeviceInfoList", "DeviceInfoSet", "HDEVINFO")]
        [InlineData("Windows.Win32.System.Diagnostics.Etw", "EventUnregister", "RegHandle", "REGHANDLE")]
        [InlineData("Windows.Win32.Security.Authentication.Identity", "LsaClose", "ObjectHandle", "LSA_HANDLE")]
        [InlineData("Windows.Win32.System.ApplicationInstallationAndServicing", "MsiSetExternalUIRecord", "puiHandler", "INSTALLUI_HANDLER_RECORD")]
        [InlineData("Windows.Win32.System.ApplicationInstallationAndServicing", "MsiSetExternalUIRecord", "ppuiPrevHandler", "PINSTALLUI_HANDLER_RECORD")]
        public void CanonicalHandlesKeepTheirSdkNames(string expectedNamespace, string methodName, string parameterName, string expectedType)
        {
            var typeSystem = DecompilerUtils.LoadDecompilerTypeSystem(TestCommon.TestUtils.Win32WinmdPath);
            var type = DecompilerUtils.GetSelfDefinedWinmdToplevelTypes(typeSystem)
                .Single(type => type.FullName == expectedNamespace + ".Apis");
            var method = type.Methods.Single(method => method.Name == methodName);
            Assert.Equal(expectedType, method.Parameters.Single(parameter => parameter.Name == parameterName).Type.Name);
        }

        [Theory]
        [InlineData("Windows.Win32.System.Performance", "PdhGetDllVersion", "PDH_STATUS")]
        [InlineData("Windows.Win32.Graphics.GdiPlus", "GdipLoadImageFromFile", "GpStatus")]
        public void NativeReturnAliasesKeepTheirSdkNames(string expectedNamespace, string methodName, string expectedType)
        {
            var typeSystem = DecompilerUtils.LoadDecompilerTypeSystem(TestCommon.TestUtils.Win32WinmdPath);
            var type = DecompilerUtils.GetSelfDefinedWinmdToplevelTypes(typeSystem)
                .Single(type => type.FullName == expectedNamespace + ".Apis");
            Assert.Equal(expectedType, type.Methods.Single(method => method.Name == methodName).ReturnType.Name);
        }
    }
}
