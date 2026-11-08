using SummerSync.Core.Net;

namespace SummerSync.Core.Tests.Net;

public class NetCommandTests
{
    private const int DefaultPort = 27016;

    private static NetCommand Parse(params string[] args) => NetCommand.Parse(args, DefaultPort);

    [Theory]
    [InlineData("status", NetCommandKind.Status)]
    [InlineData("help", NetCommandKind.Help)]
    [InlineData("?", NetCommandKind.Help)]
    [InlineData("loopback", NetCommandKind.Loopback)]
    [InlineData("HOST", NetCommandKind.HostSteam)]
    [InlineData("invite", NetCommandKind.Invite)]
    [InlineData("leave", NetCommandKind.Leave)]
    [InlineData("disconnect", NetCommandKind.Leave)]
    public void Simple_commands(string verb, NetCommandKind expected)
    {
        NetCommand command = Parse(verb);

        Assert.Equal(expected, command.Kind);
        Assert.Null(command.Error);
    }

    [Fact]
    public void No_arguments_means_status()
    {
        Assert.Equal(NetCommandKind.Status, Parse().Kind);
        Assert.Equal(NetCommandKind.Status, NetCommand.Parse(null!, DefaultPort).Kind);
    }

    [Fact]
    public void Unknown_commands_are_reported_not_thrown()
    {
        NetCommand command = Parse("hots");

        Assert.Equal(NetCommandKind.Help, command.Kind);
        Assert.Equal("Unknown command 'hots'.", command.Error);
    }

    [Fact]
    public void Udp_host_defaults_to_this_machine_only()
    {
        NetCommand command = Parse("udphost");

        Assert.Equal((NetCommandKind.HostUdp, DefaultPort, false), (command.Kind, command.Port, command.Lan));
        Assert.Null(command.Error);
    }

    [Theory]
    [InlineData(new[] { "udphost", "30000" }, 30000, false)]
    [InlineData(new[] { "udphost", "lan" }, DefaultPort, true)]
    [InlineData(new[] { "udphost", "30000", "LAN" }, 30000, true)]
    [InlineData(new[] { "udphost", "lan", "30000" }, 30000, true)]
    public void Udp_host_takes_a_port_and_lan_in_any_order(string[] args, int port, bool lan)
    {
        NetCommand command = Parse(args);

        Assert.Equal((port, lan), (command.Port, command.Lan));
        Assert.Null(command.Error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("-5")]
    [InlineData("abc")]
    public void Udp_host_rejects_bad_ports(string port)
    {
        Assert.NotNull(Parse("udphost", port).Error);
    }

    [Theory]
    [InlineData(new[] { "udpjoin" }, "127.0.0.1", DefaultPort)]
    [InlineData(new[] { "udpjoin", "30000" }, "127.0.0.1", 30000)]
    [InlineData(new[] { "udpjoin", "192.168.1.20" }, "192.168.1.20", DefaultPort)]
    [InlineData(new[] { "udpjoin", "192.168.1.20", "30000" }, "192.168.1.20", 30000)]
    public void Udp_join_defaults_to_this_machine(string[] args, string address, int port)
    {
        NetCommand command = Parse(args);

        Assert.Equal((NetCommandKind.JoinUdp, address, port), (command.Kind, command.Address, command.Port));
        Assert.Null(command.Error);
    }

    [Fact]
    public void Udp_join_rejects_nonsense()
    {
        Assert.NotNull(Parse("udpjoin", "somewhere").Error);
        Assert.NotNull(Parse("udpjoin", "1.2.3.4", "5", "6").Error);
    }

    [Theory]
    [InlineData(new[] { "sim", "200" }, 200f, 0f, 0f)]
    [InlineData(new[] { "sim", "200", "30" }, 200f, 30f, 0f)]
    [InlineData(new[] { "sim", "200", "30", "5" }, 200f, 30f, 5f)]
    [InlineData(new[] { "sim", "0", "0", "2.5" }, 0f, 0f, 2.5f)]
    [InlineData(new[] { "sim", "off" }, 0f, 0f, 0f)]
    [InlineData(new[] { "sim", "10", "0", "400" }, 10f, 0f, 100f)]
    public void Simulator_values(string[] args, float latency, float jitter, float loss)
    {
        NetCommand command = Parse(args);

        Assert.Equal(NetCommandKind.Simulate, command.Kind);
        Assert.Null(command.Error);
        Assert.Equal((latency, jitter, loss), (command.LatencyMs, command.JitterMs, command.LossPercent));
    }

    [Theory]
    [InlineData("sim")]
    [InlineData("sim fast")]
    [InlineData("sim -1")]
    [InlineData("sim NaN")]
    [InlineData("sim 2,5")]
    [InlineData("sim 1 2 3 4")]
    public void Simulator_rejects_bad_input(string line)
    {
        Assert.NotNull(Parse(line.Split(' ')).Error);
    }
}
