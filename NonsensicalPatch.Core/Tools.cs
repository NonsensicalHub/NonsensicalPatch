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

        public static string ToShortSizeString(this long size)
        {
            double bytes = size;
            if (bytes < 1024)
            {
                return bytes.ToString("f2") + "B";
            }

            bytes /= 1024;
            if (bytes < 1024)
            {
                return bytes.ToString("f2") + "KB";
            }

            bytes /= 1024;
            if (bytes < 1024)
            {
                return bytes.ToString("f2") + "MB";
            }

            bytes /= 1024;
            if (bytes < 1024)
            {
                return bytes.ToString("f2") + "GB";
            }

            bytes /= 1024;
            return bytes.ToString("f2") + "TB";
        }
    }
}
