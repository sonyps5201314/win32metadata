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
    }
}
