using System.Net;
using AwesomeAssertions;
using MMCA.Common.Infrastructure.Http;

namespace MMCA.Common.Infrastructure.Tests.Webhooks;

/// <summary>
/// <see cref="OutboundAddressPolicy"/> (design 35.19 and 35.20, review amendments A1 and A2): the deny
/// list every outbound webhook address is checked against, and the target URI shape. Each range of
/// rule 35.19 is pinned at its first and last address, with the public neighbours just outside it,
/// so a range written one bit too wide or too narrow fails here.
/// </summary>
public sealed class OutboundAddressPolicyTests
{
    // == Rule 35.19: every denied IPv4 range, first and last address (plus a notable one inside) ==
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.255.255.255")]
    [InlineData("10.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("100.64.0.0")]
    [InlineData("100.127.255.255")]
    [InlineData("127.0.0.0")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.255")]
    [InlineData("169.254.0.0")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.255.255")]
    [InlineData("172.16.0.0")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.0")]
    [InlineData("192.0.0.255")]
    [InlineData("192.168.0.0")]
    [InlineData("192.168.255.255")]
    [InlineData("198.18.0.0")]
    [InlineData("198.19.255.255")]
    [InlineData("224.0.0.0")]
    [InlineData("239.255.255.255")]
    [InlineData("240.0.0.0")]
    [InlineData("255.255.255.255")]
    public void IsPublicAddress_DeniedIPv4Range_IsFalse(string address) =>
        OutboundAddressPolicy.IsPublicAddress(IPAddress.Parse(address)).Should().BeFalse($"{address} is inside a denied IPv4 range");

    // == Rule 35.19: every denied IPv6 range (A2 adds 64:ff9b::/96 and fec0::/10) ==
    [Theory]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("64:ff9b::")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("64:ff9b::ffff:ffff")]
    [InlineData("fc00::")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fdff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("fe80::")]
    [InlineData("fe80::1")]
    [InlineData("febf:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("fec0::")]
    [InlineData("fec0::1")]
    [InlineData("feff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    [InlineData("ff00::")]
    [InlineData("ff02::1")]
    [InlineData("ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff")]
    public void IsPublicAddress_DeniedIPv6Range_IsFalse(string address) =>
        OutboundAddressPolicy.IsPublicAddress(IPAddress.Parse(address)).Should().BeFalse($"{address} is inside a denied IPv6 range");

    // == Rule 35.19: the single cloud platform endpoint inside public space (A2) ==
    [Fact]
    public void IsPublicAddress_CloudPlatformEndpoint_IsFalse() =>
        OutboundAddressPolicy.IsPublicAddress(IPAddress.Parse("168.63.129.16")).Should().BeFalse(
            "168.63.129.16 is the cloud platform endpoint, reachable from every VM although it sits in public space");

    [Theory]
    [InlineData("168.63.129.15")]
    [InlineData("168.63.129.17")]
    public void IsPublicAddress_NeighboursOfTheCloudPlatformEndpoint_AreTrue(string address) =>
        OutboundAddressPolicy.IsPublicAddress(IPAddress.Parse(address)).Should().BeTrue("only the single address is denied, not its /24");

    // == Rule 35.19: an IPv4-mapped IPv6 address is judged as its IPv4 form ==
    [Theory]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("::ffff:168.63.129.16")]
    public void IsPublicAddress_IPv4MappedPrivate_IsFalse(string address) =>
        OutboundAddressPolicy.IsPublicAddress(IPAddress.Parse(address)).Should().BeFalse("a mapped address is evaluated as the IPv4 address it carries");

    [Theory]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("::ffff:1.1.1.1")]
    public void IsPublicAddress_IPv4MappedPublic_IsTrue(string address) =>
        OutboundAddressPolicy.IsPublicAddress(IPAddress.Parse(address)).Should().BeTrue();

    // == Public addresses, including the neighbours just outside each denied range ==
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("1.0.0.0")]
    [InlineData("9.255.255.255")]
    [InlineData("11.0.0.0")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("126.255.255.255")]
    [InlineData("128.0.0.0")]
    [InlineData("169.253.255.255")]
    [InlineData("169.255.0.0")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("192.0.1.0")]
    [InlineData("192.167.255.255")]
    [InlineData("192.169.0.0")]
    [InlineData("198.17.255.255")]
    [InlineData("198.20.0.0")]
    [InlineData("223.255.255.255")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2001:4860:4860::8888")]
    public void IsPublicAddress_PublicAddress_IsTrue(string address) =>
        OutboundAddressPolicy.IsPublicAddress(IPAddress.Parse(address)).Should().BeTrue($"{address} is outside every denied range");

    // == Rule 35.20 with allowInsecure false ==
    [Theory]
    [InlineData("https://hooks.example.com/in")]
    [InlineData("https://hooks.example.com:8443/in?tenant=a")]
    [InlineData("https://8.8.8.8/in")]
    [InlineData("https://[2606:4700:4700::1111]/in")]
    public void IsAcceptableTargetUri_Secure_PublicHttpsTarget_IsAccepted(string target) =>
        OutboundAddressPolicy.IsAcceptableTargetUri(new Uri(target), allowInsecure: false).Should().BeTrue();

    [Theory]
    [InlineData("http://hooks.example.com/in")]
    [InlineData("http://localhost:5005/hook")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://localhost:5005/hook")]
    [InlineData("https://api.localhost/hook")]
    [InlineData("https://LOCALHOST/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://168.63.129.16/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[fd00::1]/hook")]
    [InlineData("https://[::ffff:10.0.0.1]/hook")]
    public void IsAcceptableTargetUri_Secure_InsecureOrPrivateTarget_IsRefused(string target) =>
        OutboundAddressPolicy.IsAcceptableTargetUri(new Uri(target), allowInsecure: false).Should().BeFalse(
            "without the development switch a target must be https, not localhost, and not a private IP literal");

    // == Rule 35.20 with allowInsecure true (A1: scheme AND address checks relaxed) ==
    [Theory]
    [InlineData("http://localhost:5005/hook")]
    [InlineData("http://api.localhost/hook")]
    [InlineData("http://10.0.0.5/hook")]
    [InlineData("http://127.0.0.1:5005/hook")]
    [InlineData("http://[::1]:5005/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("http://hooks.example.com/in")]
    [InlineData("https://hooks.example.com/in")]
    public void IsAcceptableTargetUri_Insecure_LocalAndHttpTargets_AreAccepted(string target) =>
        OutboundAddressPolicy.IsAcceptableTargetUri(new Uri(target), allowInsecure: true).Should().BeTrue(
            "the development switch skips the https, localhost and IP-literal checks so a local receiver can be targeted");

    // == Rule 35.20: scheme and user info are refused for both values ==
    [Theory]
    [InlineData("ftp://hooks.example.com/in", false)]
    [InlineData("ftp://hooks.example.com/in", true)]
    [InlineData("file:///c:/hooks/in", false)]
    [InlineData("file:///c:/hooks/in", true)]
    [InlineData("ws://hooks.example.com/in", true)]
    [InlineData("https://user:secret@hooks.example.com/in", false)]
    [InlineData("https://user:secret@hooks.example.com/in", true)]
    [InlineData("https://user@hooks.example.com/in", false)]
    [InlineData("http://user@localhost:5005/hook", true)]
    public void IsAcceptableTargetUri_WrongSchemeOrUserInfo_IsRefusedForBothValues(string target, bool allowInsecure) =>
        OutboundAddressPolicy.IsAcceptableTargetUri(new Uri(target), allowInsecure).Should().BeFalse(
            "scheme https or http, no user info and a non-empty host are required whatever allowInsecure says");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsAcceptableTargetUri_RelativeUri_IsRefused(bool allowInsecure) =>
        OutboundAddressPolicy.IsAcceptableTargetUri(new Uri("/hooks/in", UriKind.Relative), allowInsecure).Should().BeFalse(
            "a relative URI has no scheme and no host");
}
