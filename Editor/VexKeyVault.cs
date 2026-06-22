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
    [InitializeOnLoad]
    internal static class VexKeyVault
    {
        private const string k_PwPref = "vex.keys.masterpw";
        private const int k_Iterations = 100_000;

        static VexKeyVault()
        {
            VaultPath = Path.Combine(Directory.GetParent(Application.dataPath)!.FullName, "ProjectSettings",
                "VexKeyVault.json");
            FlueService.EnvProvider = AsEnv;
        }

        public static string MasterPassword
        {
            get => EditorPrefs.GetString(k_PwPref, "");
            set => EditorPrefs.SetString(k_PwPref, value ?? "");
        }

        public static bool HasMasterPassword => !string.IsNullOrEmpty(MasterPassword);
        public static bool VaultFileExists => File.Exists(VaultPath);
        public static string VaultPath { get; }

        private static VaultFile ReadFile()
        {
            if (!File.Exists(VaultPath))
                return new VaultFile();
            try
            {
                return JsonConvert.DeserializeObject<VaultFile>(File.ReadAllText(VaultPath)) ?? new VaultFile();
            }
            catch
            {
                return new VaultFile();
            }
        }

        private static void WriteFile(VaultFile vf)
        {
            File.WriteAllText(VaultPath, JsonConvert.SerializeObject(vf, Formatting.Indented));
        }

        public static Policy LoadPolicy()
        {
            return ReadFile().policy ?? new Policy();
        }

        public static void SavePolicy(Policy p)
        {
            var vf = ReadFile();
            vf.policy = p;
            WriteFile(vf);
        }

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
            var vf = ReadFile();
            var json = JsonConvert.SerializeObject(keys ?? new List<Key>());
            vf.data = Encrypt(json, MasterPassword, out var salt, out var iv);
            vf.salt = salt;
            vf.iv = iv;
            WriteFile(vf);
        }

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
                    k.kind, k.apiKey, k.model, k.priority, k.peakGuard, k.label, k.scope
                }).ToList();
                if (clean.Count > 0)
                    e["VEX_KEYS_JSON"] = JsonConvert.SerializeObject(clean);
            }

            return e;
        }

        private static string Encrypt(string plain, string password, out string saltB64, out string ivB64)
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

        private static string Decrypt(string ctB64, string password, string saltB64, string ivB64)
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

        private static byte[] RandomBytes(int n)
        {
            var b = new byte[n];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(b);
            return b;
        }

        [Serializable]
        public sealed class Key
        {
            public string id = "";
            public string kind = "glm";
            public string apiKey = "";
            public string model = "glm-4.7";
            public int priority = 100;
            public bool peakGuard = true;
            public string label = "";
            public string scope = "private";
        }

        [Serializable]
        public sealed class Policy
        {
            public string defaultChatModel = "default";
            public bool peakGuard = true;
            public string plan = "";
        }

        [Serializable]
        private sealed class VaultFile
        {
            public Policy policy = new();
            public string salt;
            public string iv;
            public string data;
        }
    }
}