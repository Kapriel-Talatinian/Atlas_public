using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Nethereum.Signer;
using Nethereum.Util;

namespace Atlas.Api.Services;

public interface IPolymarketSigningService
{
    bool IsConfigured { get; }
    string WalletAddress { get; }
    PolymarketSignedOrder SignOrder(PolymarketOrderPayload order);
    string GenerateHmacSignature(string method, string path, string body, long timestamp);
}

public sealed record PolymarketOrderPayload(
    string TokenId,
    double Price,
    double Size,
    PolymarketOrderSide Side,
    string FeeRateBps = "0",
    string Nonce = "",
    long Expiration = 0);

public sealed record PolymarketSignedOrder(
    string TokenId,
    double Price,
    double Size,
    PolymarketOrderSide Side,
    string FeeRateBps,
    string Nonce,
    long Expiration,
    string Signature,
    string Owner);

public enum PolymarketOrderSide
{
    Buy = 0,
    Sell = 1
}

public sealed class PolymarketSigningService : IPolymarketSigningService
{
    // Polymarket CTF Exchange on Polygon
    private const string ExchangeAddress = "0x4bFb41d5B3570DeFd03C39a9A4D8dE6Bd8B8982E";
    private const int ChainId = 137; // Polygon mainnet

    private readonly EthECKey? _ecKey;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _apiPassphrase;
    private readonly ILogger<PolymarketSigningService> _logger;

    public PolymarketSigningService(ILogger<PolymarketSigningService> logger)
    {
        _logger = logger;
        _apiKey = Environment.GetEnvironmentVariable("POLYMARKET_API_KEY")?.Trim() ?? string.Empty;
        _apiSecret = Environment.GetEnvironmentVariable("POLYMARKET_API_SECRET")?.Trim() ?? string.Empty;
        _apiPassphrase = Environment.GetEnvironmentVariable("POLYMARKET_API_PASSPHRASE")?.Trim() ?? string.Empty;

        string privateKey = Environment.GetEnvironmentVariable("POLYMARKET_PRIVATE_KEY")?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(privateKey))
        {
            try
            {
                _ecKey = new EthECKey(privateKey);
                _logger.LogInformation("Polymarket signer loaded for address {Address}", _ecKey.GetPublicAddress());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load Polymarket private key");
                _ecKey = null;
            }
        }
    }

    public bool IsConfigured => _ecKey is not null
        && !string.IsNullOrWhiteSpace(_apiKey)
        && !string.IsNullOrWhiteSpace(_apiSecret);

    public string WalletAddress => _ecKey?.GetPublicAddress() ?? string.Empty;

    public PolymarketSignedOrder SignOrder(PolymarketOrderPayload order)
    {
        if (_ecKey is null)
            throw new InvalidOperationException("Polymarket signer is not configured. Set POLYMARKET_PRIVATE_KEY.");

        string nonce = string.IsNullOrWhiteSpace(order.Nonce)
            ? GenerateNonce()
            : order.Nonce;

        long expiration = order.Expiration > 0
            ? order.Expiration
            : DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();

        // Build the EIP-712 order struct hash for Polymarket CTF Exchange
        byte[] structHash = BuildOrderStructHash(
            order.TokenId,
            order.Price,
            order.Size,
            order.Side,
            order.FeeRateBps,
            nonce,
            expiration);

        byte[] domainSeparator = BuildDomainSeparator();
        byte[] digest = BuildEip712Digest(domainSeparator, structHash);

        EthECDSASignature signature = _ecKey.SignAndCalculateV(digest);
        string sigHex = ToSignatureHex(signature);

        return new PolymarketSignedOrder(
            TokenId: order.TokenId,
            Price: order.Price,
            Size: order.Size,
            Side: order.Side,
            FeeRateBps: order.FeeRateBps,
            Nonce: nonce,
            Expiration: expiration,
            Signature: sigHex,
            Owner: _ecKey.GetPublicAddress());
    }

    public string GenerateHmacSignature(string method, string path, string body, long timestamp)
    {
        if (string.IsNullOrWhiteSpace(_apiSecret))
            throw new InvalidOperationException("POLYMARKET_API_SECRET is not configured.");

        // Polymarket uses URL-safe base64 for the API secret (- and _ instead of + and /).
        // Convert back to standard base64 before decoding.
        string secretB64 = _apiSecret.Replace('-', '+').Replace('_', '/');
        int padding = secretB64.Length % 4;
        if (padding > 0) secretB64 = secretB64.PadRight(secretB64.Length + (4 - padding), '=');

        string message = $"{timestamp}{method.ToUpperInvariant()}{path}{body}";
        byte[] secretBytes = Convert.FromBase64String(secretB64);
        byte[] messageBytes = Encoding.UTF8.GetBytes(message);

        using var hmac = new HMACSHA256(secretBytes);
        byte[] hash = hmac.ComputeHash(messageBytes);

        // Polymarket expects URL-safe base64 in the signature header.
        return Convert.ToBase64String(hash).Replace('+', '-').Replace('/', '_');
    }

    private byte[] BuildDomainSeparator()
    {
        byte[] typeHash = new Sha3Keccack().CalculateHash(
            Encoding.UTF8.GetBytes("EIP712Domain(string name,string version,uint256 chainId,address verifyingContract)"));
        byte[] nameHash = new Sha3Keccack().CalculateHash(Encoding.UTF8.GetBytes("Polymarket CTF Exchange"));
        byte[] versionHash = new Sha3Keccack().CalculateHash(Encoding.UTF8.GetBytes("1"));
        byte[] chainIdBytes = PadUint256(new BigInteger(ChainId));
        byte[] contractBytes = PadAddress(ExchangeAddress);

        return new Sha3Keccack().CalculateHash(
            ConcatBytes(typeHash, nameHash, versionHash, chainIdBytes, contractBytes));
    }

    private static byte[] BuildOrderStructHash(
        string tokenId,
        double price,
        double size,
        PolymarketOrderSide side,
        string feeRateBps,
        string nonce,
        long expiration)
    {
        // Polymarket uses a fixed-point representation: price * 10^20 for makerAmount/takerAmount
        // Order struct: salt, maker, signer, taker, tokenId, makerAmount, takerAmount, expiration, nonce, feeRateBps, side, signatureType
        BigInteger scaledSize = ToClobDecimal(size);
        BigInteger scaledPrice = ToClobDecimal(price);
        BigInteger makerAmount = side == PolymarketOrderSide.Buy ? scaledSize * scaledPrice / BigInteger.Pow(10, 20) : scaledSize;
        BigInteger takerAmount = side == PolymarketOrderSide.Buy ? scaledSize : scaledSize * scaledPrice / BigInteger.Pow(10, 20);

        byte[] typeHash = new Sha3Keccack().CalculateHash(
            Encoding.UTF8.GetBytes("Order(uint256 salt,address maker,address signer,address taker,uint256 tokenId,uint256 makerAmount,uint256 takerAmount,uint256 expiration,uint256 nonce,uint256 feeRateBps,uint8 side,uint8 signatureType)"));

        byte[] salt = PadUint256(new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        byte[] tokenIdBytes = PadUint256(BigInteger.Parse(tokenId));
        byte[] makerAmountBytes = PadUint256(makerAmount);
        byte[] takerAmountBytes = PadUint256(takerAmount);
        byte[] expirationBytes = PadUint256(new BigInteger(expiration));
        byte[] nonceBytes = PadUint256(BigInteger.Parse(nonce == "" ? "0" : nonce));
        byte[] feeBytes = PadUint256(BigInteger.Parse(feeRateBps));
        byte[] sideBytes = PadUint256(new BigInteger((int)side));
        byte[] sigTypeBytes = PadUint256(BigInteger.Zero); // EOA

        return new Sha3Keccack().CalculateHash(
            ConcatBytes(typeHash, salt, tokenIdBytes, makerAmountBytes, takerAmountBytes,
                expirationBytes, nonceBytes, feeBytes, sideBytes, sigTypeBytes));
    }

    private static byte[] BuildEip712Digest(byte[] domainSeparator, byte[] structHash)
    {
        byte[] prefix = [0x19, 0x01];
        return new Sha3Keccack().CalculateHash(
            ConcatBytes(prefix, domainSeparator, structHash));
    }

    private static BigInteger ToClobDecimal(double value) =>
        new BigInteger(Math.Round(value * 1e20));

    private static byte[] PadUint256(BigInteger value)
    {
        byte[] bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length >= 32) return bytes[..32];
        byte[] padded = new byte[32];
        Buffer.BlockCopy(bytes, 0, padded, 32 - bytes.Length, bytes.Length);
        return padded;
    }

    private static byte[] PadAddress(string hex)
    {
        byte[] addressBytes = Convert.FromHexString(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex);
        byte[] padded = new byte[32];
        Buffer.BlockCopy(addressBytes, 0, padded, 32 - addressBytes.Length, addressBytes.Length);
        return padded;
    }

    private static string ToSignatureHex(EthECDSASignature sig)
    {
        byte[] r = sig.R;
        byte[] s = sig.S;
        byte v = (byte)(sig.V.Length > 0 ? sig.V[0] : 27);
        byte[] result = new byte[65];
        Buffer.BlockCopy(r, 0, result, 32 - r.Length, r.Length);
        Buffer.BlockCopy(s, 0, result, 64 - s.Length, s.Length);
        result[64] = v;
        return "0x" + Convert.ToHexString(result).ToLowerInvariant();
    }

    private static string GenerateNonce()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        return new BigInteger(bytes, isUnsigned: true).ToString();
    }

    private static byte[] ConcatBytes(params byte[][] arrays)
    {
        int totalLength = arrays.Sum(a => a.Length);
        byte[] result = new byte[totalLength];
        int offset = 0;
        foreach (byte[] array in arrays)
        {
            Buffer.BlockCopy(array, 0, result, offset, array.Length);
            offset += array.Length;
        }
        return result;
    }
}

public sealed class NoopPolymarketSigningService : IPolymarketSigningService
{
    public bool IsConfigured => false;
    public string WalletAddress => string.Empty;
    public PolymarketSignedOrder SignOrder(PolymarketOrderPayload order) =>
        throw new InvalidOperationException("Polymarket signing is not configured.");
    public string GenerateHmacSignature(string method, string path, string body, long timestamp) =>
        throw new InvalidOperationException("Polymarket signing is not configured.");
}
