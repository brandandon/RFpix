// PlatformTests.cs - the parts of RFpix that differ on Linux: the Trash, and serial port names.

using RFpix.Platform;
using RFpix.Radio;
using Xunit;

namespace RFpix.Tests;

public class PlatformTests
{
    [Theory]
    [InlineData("usb-Silicon_Labs_CP2105_Dual_USB_to_UART_Bridge_Controller_00A1B2C3-if00-port0",
                "Silicon Labs CP2105 Dual USB to UART Bridge Controller (Enhanced)")]
    [InlineData("usb-Silicon_Labs_CP2105_Dual_USB_to_UART_Bridge_Controller_00A1B2C3-if01-port0",
                "Silicon Labs CP2105 Dual USB to UART Bridge Controller (Standard)")]
    [InlineData("usb-Silicon_Labs_CP2102N_USB_to_UART_Bridge_Controller_IC-9700_12345678_A-if00-port0",
                "Silicon Labs CP2102N USB to UART Bridge Controller IC-9700 12345678 A")]
    [InlineData("usb-FTDI_FT232R_USB_UART_A50285BI-if00-port0", "FTDI FT232R USB UART")]
    public void Linux_port_names_are_tidied(string id, string expected) =>
        Assert.Equal(expected, SerialPorts.TidyLinuxName(id));

    [Fact]
    public void On_Linux_a_deleted_picture_goes_to_the_Trash_with_a_note_of_where_it_was()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Path.Combine(Path.GetTempPath(), "rfpix-trash-" + Guid.NewGuid().ToString("N"));
        string old = LinuxFiles.TrashFolder;
        LinuxFiles.TrashFolder = Path.Combine(root, "Trash");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Pictures"));
            string a = Path.Combine(root, "Pictures", "2026-10-20 1942 W9ABC tower.jp2");
            File.WriteAllBytes(a, new byte[] { 1 });
            LinuxFiles.MoveToTrash(a);
            File.WriteAllBytes(a, new byte[] { 2 });   // another with the same name
            LinuxFiles.MoveToTrash(a);

            Assert.False(File.Exists(a));
            string files = Path.Combine(root, "Trash", "files"), info = Path.Combine(root, "Trash", "info");
            Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(files, "2026-10-20 1942 W9ABC tower.jp2")));
            Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(Path.Combine(files, "2026-10-20 1942 W9ABC tower.2.jp2")));
            string note = File.ReadAllText(Path.Combine(info, "2026-10-20 1942 W9ABC tower.jp2.trashinfo"));
            Assert.StartsWith("[Trash Info]\n", note);
            Assert.Contains("Path=" + root.Replace(" ", "%20") + "/Pictures/2026-10-20%201942%20W9ABC%20tower.jp2\n", note);
            Assert.Matches(@"DeletionDate=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d", note);
        }
        finally
        {
            LinuxFiles.TrashFolder = old;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
