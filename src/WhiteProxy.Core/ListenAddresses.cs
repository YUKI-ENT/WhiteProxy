using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WhiteProxy.Core;

public static class ListenAddresses
{
    public const string UnavailableMessage = "待受IPがこのPCに割り当てられていません。「設定・サービス」で停止し、待受IPv4の候補からこのPCのIPを選んでください。接続元端末の範囲は許可ルールのIPv4/CIDRで指定します。";

    public static List<IPAddress> CurrentIpv4() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
        .Where(a => a.AddressFamily == AddressFamily.InterNetwork).Prepend(IPAddress.Loopback).Distinct().ToList();

    public static string? Validate(string value, IEnumerable<IPAddress> assigned)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            return "待受IPはIPv4を指定してください。CIDRは接続元端末のルールで指定します。";
        return address.Equals(IPAddress.Any) || assigned.Contains(address) ? null : UnavailableMessage;
    }

    public static void EnsureCurrent(string value)
    {
        if (Validate(value, CurrentIpv4()) is not null) throw new ListenAddressUnavailableException();
    }
}

// 診断用の公開文言は固定し、生の例外本文やネットワーク情報を含めない。
public sealed class ListenAddressUnavailableException() : InvalidOperationException(ListenAddresses.UnavailableMessage);
