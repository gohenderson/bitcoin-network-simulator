using System.Collections.Generic;
using System.Security.Cryptography;

namespace BitcoinNetworkSimulator
{
    // ------------------------------------------------------------------
    // A process-wide, append-only registry binding each node's Id to the
    // public key it signs blocks with — see BUILTBY SIGNING at the top of
    // the file. This binding is what makes a block's BuiltBy claim
    // verifiable: not the mere existence of a signature (anyone can generate
    // a keypair and sign something), but that the signature verifies against
    // THIS registry's independently-established record of which key belongs
    // to which name — established once, the moment a node comes online (see
    // SoloMiner's constructor), before that name could ever legitimately
    // appear as BuiltBy in any block. An Impersonator can still put any name
    // it likes in BuiltBy, but it can only sign with its own real key, which
    // won't verify against whatever key is actually registered for the name
    // it's framing.
    //
    // Every node in this simulation runs in the same process, so an
    // in-memory static table is a faithful enough stand-in for what a real
    // network would need some independent channel (a genesis validator list,
    // an on-chain registration transaction, a PKI) to establish — the point
    // being demonstrated is the verification mechanism, not how identities
    // get bootstrapped in the first place.
    // ------------------------------------------------------------------
    public static class NodeIdentityRegistry
    {
        private static readonly object Lock = new();
        private static readonly Dictionary<string, byte[]> PublicKeysById = new();

        // A no-op if `nodeId` is already registered — a node's key is fixed
        // for its lifetime (including across restarts, since SoloMiner is
        // handed a key loaded from disk when one already exists), so the
        // first registration is always the durable one.
        public static void Register(string nodeId, byte[] publicKey)
        {
            lock (Lock)
            {
                if (!PublicKeysById.ContainsKey(nodeId))
                    PublicKeysById[nodeId] = publicKey;
            }
        }

        public static byte[]? GetPublicKey(string nodeId)
        {
            lock (Lock)
            {
                return PublicKeysById.TryGetValue(nodeId, out var key) ? key : null;
            }
        }

        // Verifies that `signatureHex` is a valid ECDSA signature over the
        // raw bytes of `hashHex`, produced by the private key matching
        // `publicKey`. Malformed hex in either field is treated as "does not
        // verify" rather than thrown, since both ultimately come from
        // untrusted, network-supplied block data.
        public static bool Verify(byte[] publicKey, string hashHex, string signatureHex)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                var hashBytes = System.Convert.FromHexString(hashHex);
                var signatureBytes = System.Convert.FromHexString(signatureHex);
                return ecdsa.VerifyHash(hashBytes, signatureBytes);
            }
            catch (System.FormatException)
            {
                return false;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }
}
