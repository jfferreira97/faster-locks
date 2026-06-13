using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using FasterAlerts.Models;
using Microsoft.Extensions.Logging;

namespace FasterAlerts.Services;

public class StreamflowParserService(HttpClient http, IConfiguration config, ILogger<StreamflowParserService> logger)
{
    private static readonly HashSet<string> StreamflowProgramIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "strmRqUCoQUgGUan5YhzUZa6KqdzwX5L6FpUxfmKg5m",
        "aSTRM2NKoKxNnkmLWk9sz3k74gKBk9t7bpPrTGxMszH"
    };

    // Streamflow stream account byte offsets (from @streamflow/stream SDK constants)
    private const int OFF_END   = 44;   // u64 — vesting end unix timestamp
    private const int OFF_START = 420;  // u64 — vesting start unix timestamp
    private const int OFF_CLIFF = 452;  // u64 — cliff unix timestamp

    public async Task<StreamAlert?> ParseAsync(EnhancedTransaction tx)
    {
        if (tx.TransactionError is not null) return null;

        var sfIx = tx.Instructions
            .FirstOrDefault(ix => ix.ProgramId is not null && StreamflowProgramIds.Contains(ix.ProgramId));

        if (sfIx is null) return null;

        logger.LogInformation("📦 Streamflow tx {Sig} | feePayer={FeePayer} | accounts[0..5]={Accounts}",
            tx.Signature?[..8], tx.FeePayer?[..8],
            string.Join(", ", sfIx.Accounts.Take(6).Select(a => a[..8])));

        // accounts[0]=sender, [1]=senderTokens, [2]=recipient, [3]=streamMetadata(PDA), [4]=escrowTokens
        var escrowAccount = sfIx.Accounts.ElementAtOrDefault(4) ?? "";
        var streamAccount = sfIx.Accounts.ElementAtOrDefault(3) ?? "";
        var recipient     = sfIx.Accounts.ElementAtOrDefault(2) ?? "";

        var transfer = tx.TokenTransfers
            .FirstOrDefault(t => t.TokenAmount > 0 &&
                (t.ToUserAccount == escrowAccount || t.ToTokenAccount == escrowAccount));

        if (transfer is null)
        {
            transfer = tx.TokenTransfers
                .Where(t => t.FromUserAccount == tx.FeePayer && t.TokenAmount > 0)
                .MaxBy(t => t.TokenAmount);
        }

        if (transfer is null)
        {
            logger.LogInformation("⏭  skip {Sig} — no lock transfer found, probably withdraw/cancel", tx.Signature?[..8]);
            return null;
        }

        var txTime = DateTimeOffset.FromUnixTimeSeconds(tx.Timestamp);

        DateTimeOffset? unlockDate = null;
        int cliffDays = -1, vestingDays = -1;

        // Primary: read stream state account via Helius getAccountInfo
        var onChain = await FetchStreamAccountAsync(streamAccount);
        if (onChain.HasValue)
        {
            var (acStart, acEnd, acCliff) = onChain.Value;
            vestingDays = (int)Math.Round((acEnd - acStart) / 86400.0);
            cliffDays   = acCliff > acStart ? (int)Math.Round((acCliff - acStart) / 86400.0) : 0;
            unlockDate  = acCliff > acStart
                ? DateTimeOffset.FromUnixTimeSeconds(acCliff)
                : DateTimeOffset.FromUnixTimeSeconds(acEnd);
            logger.LogInformation("📐 on-chain | start={S} end={E} cliff={C} → vesting={V}d cliff={Cd}d",
                acStart, acEnd, acCliff, vestingDays, cliffDays);
        }
        else
        {
            // Fallback: Borsh-decode instruction data (unreliable across program versions)
            var decoded = TryDecodeCreateParams(sfIx.Data);
            if (decoded.HasValue)
            {
                var (startTime, cliff, period, amtPerPeriod, netAmount) = decoded.Value;

                if (cliff > startTime)
                {
                    unlockDate = DateTimeOffset.FromUnixTimeSeconds(cliff);
                    cliffDays  = (int)Math.Round((cliff - startTime) / 86400.0);
                }
                else if (period > 0)
                {
                    unlockDate = DateTimeOffset.FromUnixTimeSeconds(startTime + period);
                    cliffDays  = 0;
                }

                if (amtPerPeriod > 0 && period > 0 && netAmount > 0)
                {
                    var totalPeriods   = (long)Math.Ceiling((double)netAmount / amtPerPeriod);
                    var vestingEndUnix = startTime + totalPeriods * period;
                    var candidate      = (int)Math.Round((vestingEndUnix - startTime) / 86400.0);
                    vestingDays = candidate is >= 0 and <= 36500 ? candidate : -1;
                }

                logger.LogInformation("📐 Borsh fallback | cliff={Cd}d vesting={V}d", cliffDays, vestingDays);
            }
        }

        // Cliff-based locks have period=1s so period math gives vestingDays=0.
        // UnlockDate is always set correctly from cliff/end; derive vestingDays from it as fallback.
        if (vestingDays <= 0 && unlockDate.HasValue)
        {
            vestingDays = Math.Max(1, (int)Math.Round((unlockDate.Value - txTime).TotalDays));
            logger.LogInformation("📐 vesting derived from unlock date: {V}d", vestingDays);
        }

        logger.LogInformation("🔒 stream CREATE | mint={Mint} | amount={Amount:N0} | vesting={V}d cliff={C}d",
            transfer.Mint?[..8], transfer.TokenAmount, vestingDays, cliffDays);

        return new StreamAlert
        {
            Signature       = tx.Signature ?? "",
            StreamAccount   = streamAccount,
            TokenMint       = transfer.Mint ?? "",
            AmountLocked    = transfer.TokenAmount,
            CreatorWallet   = tx.FeePayer ?? "",
            RecipientWallet = recipient,
            CliffDays       = cliffDays,
            VestingDays     = vestingDays,
            UnlockDate      = unlockDate,
            Timestamp       = txTime
        };
    }

    // Reads the stream state account directly from Solana via Helius and decodes the
    // start/end/cliff timestamps using published Streamflow SDK byte offsets.
    private async Task<(long start, long end, long cliff)?> FetchStreamAccountAsync(string streamAccount)
    {
        var apiKey = config["Helius:ApiKey"]
                  ?? Environment.GetEnvironmentVariable("HELIUS_API_KEY")
                  ?? "";
        if (string.IsNullOrEmpty(apiKey)) return null;

        var rpc  = $"https://mainnet.helius-rpc.com/?api-key={apiKey}";
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "getAccountInfo",
            @params = new object[] { streamAccount, new { encoding = "base64" } }
        });

        // One retry — account is available as soon as Helius delivers the webhook
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(800);
            try
            {
                var resp    = await http.PostAsync(rpc, new StringContent(body, Encoding.UTF8, "application/json"));
                var raw     = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(raw);

                var valueEl = doc.RootElement.GetProperty("result").GetProperty("value");
                if (valueEl.ValueKind == JsonValueKind.Null) continue;

                var b64   = valueEl.GetProperty("data")[0].GetString()!;
                var bytes = Convert.FromBase64String(b64);

                if (bytes.Length < OFF_CLIFF + 8) continue;

                var end   = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(OFF_END,   8));
                var start = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(OFF_START, 8));
                var cliff = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(OFF_CLIFF, 8));

                // Plausible timestamp range: 2020–2060
                const long minTs = 1_577_836_800L;
                const long maxTs = 2_840_140_800L;
                if (start < minTs || start > maxTs || end < minTs || end > maxTs || end <= start)
                    continue;

                return (start, end, cliff > start ? cliff : start);
            }
            catch (Exception ex)
            {
                logger.LogDebug("getAccountInfo attempt {N} failed for {Acct}: {Err}",
                    attempt + 1, streamAccount[..8], ex.Message);
            }
        }

        logger.LogWarning("⚠️  stream account fetch failed for {Acct} — falling back to Borsh", streamAccount[..8]);
        return null;
    }

    // Borsh-decodes Streamflow CreateStream instruction params.
    // Unreliable across program versions — used only as last resort.
    private static (long startTime, long cliff, long period, ulong amtPerPeriod, ulong netAmount)? TryDecodeCreateParams(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        try
        {
            var bytes = Base58Decode(data);
            if (bytes.Length < 48) return null;

            var startTime = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8,  8));
            var netAmount =       BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16, 8));
            var period    = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(24, 8));
            var amtPerPd  =       BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(32, 8));
            var cliff     = (long)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(40, 8));

            return (startTime, cliff, period, amtPerPd, netAmount);
        }
        catch { return null; }
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
        var bytes   = bigInt.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leading = input.TakeWhile(c => c == '1').Count();
        if (leading == 0) return bytes;
        var result = new byte[leading + bytes.Length];
        bytes.CopyTo(result, leading);
        return result;
    }
}
