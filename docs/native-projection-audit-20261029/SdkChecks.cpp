#include <type_traits>
namespace PSDK {}
#include "win32/base.h"
#if defined(_WIN64)
#include "win32/impl/Windows.Win32.Devices.DeviceAndDriverInstallation.0.h"
#endif
#include "win32/impl/Windows.Win32.System.Com.0.h"
#include "win32/impl/Windows.Win32.Graphics.Direct2D.Common.0.h"
#include "win32/impl/Windows.Win32.Graphics.Direct3D.0.h"
#include "win32/impl/Windows.Win32.Graphics.Direct2D.0.h"
#include "win32/impl/Windows.Win32.Graphics.DirectWrite.0.h"
#include "win32/impl/Windows.Win32.Graphics.Direct3D.Dxc.0.h"
#include "win32/impl/Windows.Win32.Data.Xml.XmlLite.0.h"
static_assert(std::is_same_v<ID3D10Blob, ID3DBlob>);
static_assert(std::is_same_v<ID3D10Include, ID3DInclude>);
static_assert(std::is_same_v<IDWriteGeometrySink, ID2D1SimplifiedGeometrySink>);
static_assert(std::is_same_v<IDxcBlobWide, IDxcBlobUtf16>);
static_assert(std::is_same_v<IXmlReaderInput, IUnknown>);
static_assert(std::is_same_v<IXmlWriterOutput, IUnknown>);
#if defined(_WIN64)
static_assert(std::is_same_v<win32::Windows::Win32::Devices::DeviceAndDriverInstallation::PRIORITY, uint32_t>);
#endif
int main() { return 0; }
