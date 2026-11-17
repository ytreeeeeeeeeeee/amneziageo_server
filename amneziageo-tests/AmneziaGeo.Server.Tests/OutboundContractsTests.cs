using System.Text.Json;
using AmneziaGeo.Server.Api.Outbounds;
using AmneziaGeo.Server.Dal;
using AmneziaGeo.Server.Routing.Outbound;

namespace AmneziaGeo.Server.Tests;

public class OutboundContractsTests
{
    private static readonly JsonSerializerOptions Json = Options();
    private static readonly OutboundAnswers Answers = new([new LocalOutboundResponses(), new AwgOutboundResponses()]);

    [Fact]
    public void CommonContractsContainNoAwgFields()
    {
        string[] awgFields =
        [
            "Host", "Port", "Proxy", "PrivateKey", "PublicKey", "PeerKey", "PresharedKey",
            "Address", "Mtu", "Keepalive", "Obfuscation", "Jc", "LastHandshake", "Endpoint"
        ];
        Type[] contracts =
        [
            typeof(OutboundConfig), typeof(OutboundEntity), typeof(OutboundRequest),
            typeof(OutboundResponse), typeof(OutboundState), typeof(OutboundStateBody)
        ];
        foreach (var contract in contracts)
        {
            Assert.DoesNotContain(contract.GetProperties(), property => awgFields.Contains(property.Name));
        }
    }

    [Theory]
    [InlineData("local", typeof(LocalOutboundRequest), typeof(LocalOutboundConfig))]
    [InlineData("wg", typeof(AwgOutboundRequest), typeof(AwgOutboundConfig))]
    [InlineData("ws", typeof(WsOutboundRequest), typeof(AwgOutboundConfig))]
    public void KindSelectsTheRequestAndConfigRegardlessOfPropertyOrder(string kind, Type requestType, Type configType)
    {
        var text = "{\"name\":\"exit\",\"isEnabled\":true,\"kind\":\"" + kind + "\"}";
        var request = JsonSerializer.Deserialize<OutboundRequest>(text, Json)!;
        Assert.IsType(requestType, request);
        Assert.Equal(kind, request.Kind);
        var config = request.ToConfig();
        Assert.IsType(configType, config);
        Assert.Equal(kind, config.Kind);
        Assert.Equal("exit", config.Name);
    }

    [Theory]
    [InlineData("host", "\"server.example.net\"")]
    [InlineData("privateKey", "\"secret\"")]
    [InlineData("obfuscation", "{}")]
    [InlineData("lastHandshake", "null")]
    public void LocalRequestsRefuseProtocolFields(string field, string value)
    {
        var text = "{\"kind\":\"local\",\"name\":\"direct\",\"" + field + "\":" + value + "}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OutboundRequest>(text, Json));
    }

    [Fact]
    public void LocalOutboundsUseTheirOwnContractsWithoutAwgFactories()
    {
        var config = LocalOutboundDefaults.Direct();
        var state = new LocalOutboundDriver().State(config);
        var entity = new LocalOutboundEntityFactory().Create(config);
        var answers = new OutboundAnswers([new LocalOutboundResponses()]);
        var response = answers.Outbound(config, state, true);

        Assert.IsType<LocalOutboundConfig>(config);
        Assert.IsType<LocalOutboundState>(state);
        Assert.IsType<LocalOutboundEntity>(entity);
        Assert.IsType<LocalOutboundResponse>(response);
        Assert.IsType<LocalOutboundStateBody>(response.State);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"direct\"}")]
    public void RequestsWithoutAKindAreRefused(string text)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OutboundRequest>(text, Json));
    }

    [Fact]
    public void CommonContractsCannotStandInForAnImplementation()
    {
        Assert.True(typeof(OutboundConfig).IsAbstract);
        Assert.True(typeof(OutboundEntity).IsAbstract);
        Assert.True(typeof(OutboundRequest).IsAbstract);
        Assert.True(typeof(OutboundResponse).IsAbstract);
    }

    [Fact]
    public void UnknownKindsAreRefused()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OutboundRequest>("{\"kind\":\"unknown\"}", Json));
    }

    [Fact]
    public void MixedResponsesKeepOnlyTheirOwnFieldsAndRedactSecrets()
    {
        var local = LocalOutboundDefaults.Direct();
        var awg = AwgOutboundDefaults.Fresh("awg-exit", OutboundKind.Ws) with
        {
            PrivateKey = "secret", PeerKey = "peer", PresharedKey = "preshared", Host = "exit.example.net",
        };
        OutboundResponse[] responses =
        [
            Answers.Outbound(local, new OutboundState(local.Name, true, null, null, true, ""), false),
            Answers.Outbound(awg,
                new AwgOutboundState(awg.Name, true, "127.0.0.1:1234", DateTimeOffset.UtcNow, 1, 2, true, ""), false),
        ];
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(responses, Json));
        var direct = doc.RootElement[0];
        var tunnel = doc.RootElement[1];
        Assert.False(direct.TryGetProperty("privateKey", out _));
        Assert.False(direct.TryGetProperty("host", out _));
        Assert.False(direct.TryGetProperty("obfuscation", out _));
        Assert.False(direct.GetProperty("state").TryGetProperty("lastHandshake", out _));
        Assert.Equal("ws", tunnel.GetProperty("kind").GetString());
        Assert.Equal("exit.example.net", tunnel.GetProperty("host").GetString());
        Assert.Equal(JsonValueKind.Null, tunnel.GetProperty("privateKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, tunnel.GetProperty("presharedKey").ValueKind);
        Assert.True(tunnel.GetProperty("state").TryGetProperty("lastHandshake", out _));
        Assert.DoesNotContain("secret", doc.RootElement.GetRawText());
    }

    [Fact]
    public void AnUnregisteredProtocolResponseCannotSilentlyLoseItsSettings()
    {
        var answers = new OutboundAnswers([]);
        Assert.Throws<InvalidOperationException>(() =>
            answers.Outbound(AwgOutboundDefaults.Fresh("exit"), null, false));
    }

    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        OutboundJson.Configure(options);
        return options;
    }
}
