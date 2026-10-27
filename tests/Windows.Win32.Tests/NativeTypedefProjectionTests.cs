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

            Assert.Equal("UInt32", method.Parameters.Single(parameter => parameter.Name == "Priority").Type.Name);
        }
    }
}
