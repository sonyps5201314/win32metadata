using MetadataUtils;
using Xunit;

namespace MetadataUtils.Tests
{
    public class EncodeHelpersTests
    {
        [Theory]
        [InlineData("Windows.Win32.Graphics.GdiPlus.InterpolationMode")]
        [InlineData("global::Windows.Win32.Graphics.GdiPlus.InterpolationMode")]
        [InlineData("Windows.Win32.Graphics.GdiPlus.Image**")]
        public void DecodeRemapPreservesQualifiedTypes(string expectedType)
        {
            Assert.True(EncodeHelpers.DecodeRemap(expectedType, out var attributes, out var type, out var name));
            Assert.Equal(expectedType, type);
            Assert.Null(attributes);
            Assert.Null(name);
        }

        [Fact]
        public void QualifiedRemapsRetainAttributesAndParameterNames()
        {
            Assert.True(EncodeHelpers.DecodeRemap("[Const]Windows.Win32.Graphics.GdiPlus.Image** frame",
                out var attributes, out var type, out var name));
            Assert.Equal("Windows.Win32.Graphics.GdiPlus.Image**", type);
            Assert.Equal("frame", name);
            Assert.Equal("Const", Assert.Single(attributes).Name.ToString());
        }
    }
}
