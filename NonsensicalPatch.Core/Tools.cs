using System.Text;

namespace NonsensicalPatch.Core
{
    public static partial class Tools
    {
        public static string ToHex(this byte[] bytes, bool upperCase = true)
        {
            StringBuilder result = new StringBuilder(bytes.Length * 2);

            for (int i = 0; i < bytes.Length; i++)
                result.Append(bytes[i].ToString(upperCase ? "X2" : "x2"));

            return result.ToString();
        }
        public static string GetTempFilePath()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "Nonsensical");
            if (Directory.Exists(tempDir) == false)
            {
                Directory.CreateDirectory(tempDir);
            }
            return Path.Combine(tempDir, Guid.NewGuid().ToString() + ".temp");
        }
    }
}
