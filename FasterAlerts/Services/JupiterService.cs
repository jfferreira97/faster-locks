using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Solnet.Wallet;

namespace FasterAlerts.Services;

public class JupiterService(HttpClient http, TradingEventLog eventLog, ILogger<JupiterService> logger)
{
    private const string SolMint = "So11111111111111111111111111111111111111112";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<(string? Signature, long OutAmount)> BuyAsync(string outputMint, long lamports, string privateKey, string walletAddress)
    {
        var order = await GetOrderAsync(SolMint, outputMint, lamports, slippageBps: 1000, walletAddress);
        if (order is null) return (null, 0);
        eventLog.Info($"Jupiter order OK | outAmount={order.OutAmount} | reqId={order.RequestId?[..8]}");
        var sig = await ExecuteAsync(order, privateKey);
        return (sig, order.OutAmount);
    }

    public async Task<string?> SellAsync(string inputMint, long tokenAmount, string privateKey, string walletAddress)
    {
        var order = await GetOrderAsync(inputMint, SolMint, tokenAmount, slippageBps: 3000, walletAddress);
        if (order is null) return null;
        return await ExecuteAsync(order, privateKey);
    }

    private async Task<JupiterOrder?> GetOrderAsync(string inputMint, string outputMint, long amount, int slippageBps, string walletAddress)
    {
        var url = $"https://api.jup.ag/ultra/v1/order?inputMint={inputMint}&outputMint={outputMint}" +
                  $"&amount={amount}&slippageBps={slippageBps}&taker={walletAddress}";
        eventLog.Info($"Jupiter GET {url.Replace(walletAddress, walletAddress[..Math.Min(8,walletAddress.Length)]+"…")}");
        try
        {
            var resp = await http.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            var headers = string.Join(", ", resp.Headers.Select(h => $"{h.Key}:{string.Join(";",h.Value)}"));
            eventLog.Info($"Jupiter response {(int)resp.StatusCode} | headers: {headers}");
            eventLog.Info($"Jupiter body: {body}");
            if (!resp.IsSuccessStatusCode)
            {
                eventLog.Error($"Jupiter order failed {resp.StatusCode}: {body}");
                return null;
            }
            var order = JsonSerializer.Deserialize<JupiterOrder>(body, JsonOpts);
            if (order?.Transaction is null)
                eventLog.Error($"Jupiter order: transaction field missing in success response");
            return order;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Jupiter order request failed");
            eventLog.Error($"Jupiter order request failed: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> ExecuteAsync(JupiterOrder order, string privateKey)
    {
        if (string.IsNullOrEmpty(order.Transaction))
        {
            eventLog.Error("Jupiter: order response has no transaction field");
            return null;
        }
        if (string.IsNullOrEmpty(privateKey))
        {
            eventLog.Error("Jupiter: no private key configured — set it in Settings > Wallet");
            return null;
        }

        try
        {
            var signed = SignTransaction(order.Transaction, privateKey);
            if (signed is null)
            {
                eventLog.Error("Jupiter: transaction signing returned null — check private key format");
                return null;
            }

            var payload = JsonSerializer.Serialize(new { signedTransaction = signed, requestId = order.RequestId });
            var resp = await http.PostAsync("https://api.jup.ag/ultra/v1/execute",
                new StringContent(payload, Encoding.UTF8, "application/json"));

            var json = await resp.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<JupiterExecuteResult>(json, JsonOpts);

            if (result?.Status == "Success")
            {
                logger.LogInformation("✅ Jupiter executed | sig={Sig}", result.Signature?[..8]);
                eventLog.Info($"Jupiter executed | sig={result.Signature?[..8]}");
                return result.Signature;
            }

            var errMsg = $"Jupiter execute failed: {result?.Status} | {result?.Error}";
            logger.LogError("Jupiter execute failed: {Status} | {Error}", result?.Status, result?.Error);
            eventLog.Error(errMsg);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Jupiter execute failed");
            var keyLen = string.IsNullOrEmpty(privateKey) ? 0 : Base58Decode(privateKey).Length;
            eventLog.Error($"Jupiter execute failed: {ex.Message} | keyBytes={keyLen}");
            return null;
        }
    }

    private static string? SignTransaction(string base64Tx, string privateKeyBase58)
    {
        var txBytes = Convert.FromBase64String(base64Tx);
        var raw = Base58Decode(privateKeyBase58);
        // BigInteger.ToByteArray strips leading zero bytes — pad back to 64
        byte[] keyBytes;
        if (raw.Length == 64)
        {
            keyBytes = raw;
        }
        else if (raw.Length < 64)
        {
            keyBytes = new byte[64];
            raw.CopyTo(keyBytes, 64 - raw.Length);
        }
        else
        {
            throw new InvalidOperationException($"Unexpected keypair length {raw.Length} — expected 64 bytes");
        }
        var account = new Account(keyBytes, keyBytes[32..]);

        int offset = 0;

        // v0 versioned transactions start with a prefix byte >= 0x80
        bool isVersioned = (txBytes[0] & 0x80) != 0;
        int versionPrefixLen = isVersioned ? 1 : 0;
        if (isVersioned) offset++;

        // compact-u16 decode numSigs
        byte b = txBytes[offset++];
        int numSigs = (b & 0x80) == 0 ? b : (b & 0x7F) | (txBytes[offset++] << 7);

        int sigSlot = offset;
        int msgStart = sigSlot + numSigs * 64;

        // For versioned txs the signed message includes the version prefix byte
        byte[] message = isVersioned
            ? [..txBytes[..versionPrefixLen], ..txBytes[msgStart..]]
            : txBytes[msgStart..];

        var sig = account.Sign(message);
        sig.CopyTo(txBytes.AsSpan(sigSlot, 64));
        return Convert.ToBase64String(txBytes);
    }

    private const string B58 = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    private static byte[] Base58Decode(string input)
    {
        var bigInt = BigInteger.Zero;
        foreach (var c in input)
        {
            var digit = B58.IndexOf(c);
            if (digit < 0) throw new FormatException($"Invalid Base58 char: {c}");
            bigInt = bigInt * 58 + digit;
        }
        var bytes = bigInt.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leading = input.TakeWhile(c => c == '1').Count();
        if (leading == 0) return bytes;
        var result = new byte[leading + bytes.Length];
        bytes.CopyTo(result, leading);
        return result;
    }
}

internal class JupiterOrder
{
    [JsonPropertyName("transaction")]  public string? Transaction { get; set; }
    [JsonPropertyName("requestId")]    public string? RequestId   { get; set; }
    [JsonPropertyName("outAmount")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long    OutAmount   { get; set; }
    [JsonPropertyName("inputMint")]    public string? InputMint   { get; set; }
    [JsonPropertyName("outputMint")]   public string? OutputMint  { get; set; }
}

internal class JupiterExecuteResult
{
    [JsonPropertyName("status")]    public string? Status    { get; set; }
    [JsonPropertyName("signature")] public string? Signature { get; set; }
    [JsonPropertyName("error")]     public string? Error     { get; set; }
}
