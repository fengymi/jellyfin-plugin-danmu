using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StringMetric;

namespace Jellyfin.Plugin.Danmu.Core.Extensions
{
    public static class StringExtension
    {
        public static long ToLong(this string s)
        {
            if (long.TryParse(s, out var val))
            {
                return val;
            }

            return 0;
        }

        public static int ToInt(this string s)
        {
            if (int.TryParse(s, out var val))
            {
                return val;
            }

            return 0;
        }

        public static Int64 ToInt64(this string s)
        {
            if (Int64.TryParse(s, out var val))
            {
                return val;
            }

            return 0;
        }

        public static float ToFloat(this string s)
        {
            float val;
            if (float.TryParse(s, out val))
            {
                return val;
            }

            return 0.0f;
        }

        public static double ToDouble(this string s)
        {
            if (double.TryParse(s, out var val))
            {
                return val;
            }

            return 0.0;
        }

        public static string ToMD5(this string str)
        {
            using (var cryptoMD5 = System.Security.Cryptography.MD5.Create())
            {
                //將字串編碼成 UTF8 位元組陣列
                var bytes = Encoding.UTF8.GetBytes(str);

                //取得雜湊值位元組陣列
                var hash = cryptoMD5.ComputeHash(bytes);

                //取得 MD5
                var md5 = BitConverter.ToString(hash)
                  .Replace("-", String.Empty)
                  .ToUpper();

                return md5;
            }
        }

        public static string ToBase64(this string str)
        {
            var plainTextBytes = System.Text.Encoding.UTF8.GetBytes(str);
            return System.Convert.ToBase64String(plainTextBytes);
        }

        public static double Distance(this string s1, string s2)
        {
            var jw = new JaroWinkler();

            return jw.Similarity(s1, s2);
        }
    }
}
