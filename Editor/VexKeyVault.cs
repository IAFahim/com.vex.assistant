using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using Vex.Codex.Editor;

namespace Vex.Assistant.Editor
{
    /// <summary>
    /// The Vex key VAULT — keys, models, and routing policy live in Unity, not flue's <c>.env</c>. Add as many keys
    /// as you like from the <b>Vex ▸ Keys &amp; Models</b> window; they are AES-256 ENCRYPTED (PBKDF2-derived key) and
    /// the ciphertext is written to <c>ProjectSettings/VexKeyVault.json</c> — safe to commit and sync across machines.
    /// The MASTER PASSWORD that unlocks them is kept in <see cref="EditorPrefs"/> (machine-local) and is NEVER in the
    /// repo — that separation is what makes the committed file safe. At flue-spawn time the keys are decrypted and
    /// passed as one env blob (<c>VEX_KEYS_JSON</c>) via <see cref="FlueService.EnvProvider"/>, so flue reads them
    /// without any plaintext on disk.
    ///
    /// HONEST SECURITY NOTE: this is real encryption that keeps keys out of plaintext and survives a casual repo leak.
    /// Its strength is your master password's secrecy (a short/guessable one weakens it) and the repo staying private.
    /// It is NOT unbreakable — treat API keys as rotatable if anything ever leaks.
    /// </summary>
    [InitializeOnLoad]
    internal static class VexKeyVault
    {
        const string k_PwPref = "vex.keys.masterpw";
        const int k_Iterations = 100_000;

        static readonly string s_VaultPath; // ProjectSettings/VexKeyVault.json (committed)

        static VexKeyVault()
        {
            // Application.dataPath is main-thread only; cache the path here (InitializeOnLoad runs on the main thread).
            s_VaultPath = Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, "ProjectSettings", "VexKeyVault.json");
            FlueService.EnvProvider = AsEnv;
        }

        // ---- models ------------------------------------------------------------------------------------------------
        [Serializable]
        public sealed class Key
        {
            public string id = "";
            public string kind = "glm";      // glm | minimax
            public string apiKey = "";
            public string model = "glm-4.7";
            public int priority = 100;        // lower = tried first
            public bool peakGuard = true;     // GLM: skip during the z.ai peak window
            public string label = "";
            public string scope = "private";  // private | shared (label only)
        }

        [Serializable]
        public sealed class Policy
        {
            public string defaultChatModel = "default"; // default | glm | glm:glm-5.2
            public bool peakGuard = true;               // global GLM peak pause
            public string plan = "";                    // (none) | lite | pro | max
        }

        [Serializable]
        sealed class VaultFile
        {
            public Policy policy = new Policy();
            public string salt;   // base64 (PBKDF2 salt)
            public string iv;     // base64 (AES IV)
            public string data;   // base64 (AES-encrypted JSON array of Key)
        }

        // ---- master password (machine-local, EditorPrefs) ----------------------------------------------------------
        public static string MasterPassword
        {
            get => EditorPrefs.GetString(k_PwPref, "");
            set => EditorPrefs.SetString(k_PwPref, value ?? "");
        }

        public static bool HasMasterPassword => !string.IsNullOrEmpty(MasterPassword);
        public static bool VaultFileExists => File.Exists(s_VaultPath);
        public static string VaultPath => s_VaultPath;

        // ---- load / save -------------------------------------------------------------------------------------------
        static VaultFile ReadFile()
        {
            if (!File.Exists(s_VaultPath))
                return new VaultFile();
            try { return JsonConvert.DeserializeObject<VaultFile>(File.ReadAllText(s_VaultPath)) ?? new VaultFile(); }
            catch { return new VaultFile(); }
        }

        static void WriteFile(VaultFile vf) => File.WriteAllText(s_VaultPath, JsonConvert.SerializeObject(vf, Formatting.Indented));

        public static Policy LoadPolicy() => ReadFile().policy ?? new Policy();

        public static void SavePolicy(Policy p)
        {
            var vf = ReadFile();
            vf.policy = p;
            WriteFile(vf);
        }

        /// <summary>Decrypt the key list. Returns null and sets <paramref name="error"/> when locked or the password is
        /// wrong; returns an empty list when no keys are stored yet.</summary>
        public static List<Key> LoadKeys(out string error)
        {
            error = null;
            var vf = ReadFile();
            if (string.IsNullOrEmpty(vf.data))
                return new List<Key>();
            if (!HasMasterPassword)
            {
                error = "Locked — set the master password to unlock the keys.";
                return null;
            }
            try
            {
                var json = Decrypt(vf.data, MasterPassword, vf.salt, vf.iv);
                return JsonConvert.DeserializeObject<List<Key>>(json) ?? new List<Key>();
            }
            catch
            {
                error = "Wrong master password — cannot decrypt the keys.";
                return null;
            }
        }

        public static void SaveKeys(List<Key> keys)
        {
            if (!HasMasterPassword)
                throw new InvalidOperationException("Set a master password before saving keys.");
            var vf = ReadFile(); // preserve policy
            var json = JsonConvert.SerializeObject(keys ?? new List<Key>());
            vf.data = Encrypt(json, MasterPassword, out var salt, out var iv);
            vf.salt = salt;
            vf.iv = iv;
            WriteFile(vf);
        }

        // ---- env provider (consumed by FlueService for every flue spawn) -------------------------------------------
        public static IDictionary<string, string> AsEnv()
        {
            var e = new Dictionary<string, string>();
            var pol = LoadPolicy();
            e["GLM_DISABLE_PEAK"] = pol.peakGuard ? "1" : "0";
            if (!string.IsNullOrWhiteSpace(pol.plan))
                e["GLM_PLAN"] = pol.plan.Trim();

            var keys = LoadKeys(out _);
            if (keys != null && keys.Count > 0)
            {
                var clean = keys.Where(k => !string.IsNullOrWhiteSpace(k.apiKey)).Select(k => new
                {
                    id = string.IsNullOrWhiteSpace(k.id) ? k.label : k.id,
                    k.kind, k.apiKey, k.model, k.priority, k.peakGuard, k.label, k.scope,
                }).ToList();
                if (clean.Count > 0)
                    e["VEX_KEYS_JSON"] = JsonConvert.SerializeObject(clean);
            }
            return e;
        }

        // ---- AES-256 / PBKDF2 --------------------------------------------------------------------------------------
        static string Encrypt(string plain, string password, out string saltB64, out string ivB64)
        {
            var salt = RandomBytes(16);
            using var kdf = new Rfc2898DeriveBytes(password, salt, k_Iterations, HashAlgorithmName.SHA256);
            using var aes = Aes.Create();
            aes.Key = kdf.GetBytes(32);
            aes.GenerateIV();
            saltB64 = Convert.ToBase64String(salt);
            ivB64 = Convert.ToBase64String(aes.IV);
            using var enc = aes.CreateEncryptor();
            var bytes = Encoding.UTF8.GetBytes(plain);
            return Convert.ToBase64String(enc.TransformFinalBlock(bytes, 0, bytes.Length));
        }

        static string Decrypt(string ctB64, string password, string saltB64, string ivB64)
        {
            var salt = Convert.FromBase64String(saltB64);
            using var kdf = new Rfc2898DeriveBytes(password, salt, k_Iterations, HashAlgorithmName.SHA256);
            using var aes = Aes.Create();
            aes.Key = kdf.GetBytes(32);
            aes.IV = Convert.FromBase64String(ivB64);
            using var dec = aes.CreateDecryptor();
            var ct = Convert.FromBase64String(ctB64);
            return Encoding.UTF8.GetString(dec.TransformFinalBlock(ct, 0, ct.Length));
        }

        static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(b);
            return b;
        }
    }
}
