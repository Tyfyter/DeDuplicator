using System;
using System.Text.RegularExpressions;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace DeDuplicator {
    public static class Program {
        public static int Main(string[] args) {
            MatchCollection matches = new Regex("(?<=-)([^-\\s]*) ?([^-]*)").Matches(string.Join(" ", args));
            string folder = null;
            string output = null;//Path.Combine(AppContext.BaseDirectory, "DeDuplicated");
            string target = null;
            bool fixMode = false;
            bool hardlink = false;
            bool linkInPlace = true;
            int maxSize = 500 * 1000 * 1000;
            foreach (Match m in matches) {
                switch (m.Groups[1].ToString().ToUpper()) {
                    case "R":
                    case "REGION":
                    case "D":
                    case "DIRECTORY":
                    case "FOLDER":
                    folder = Path.GetFullPath(m.Groups[2].ToString().Trim(Path.DirectorySeparatorChar));
                    break;

                    case "T":
                    case "TARGET":
                    target = m.Groups[2].ToString();
                    break;

                    case "O":
                    case "OUTPUT":
                    output = Path.GetFullPath(m.Groups[2].ToString().Trim(Path.DirectorySeparatorChar));
                    break;

                    case "FIX":
                    fixMode = true;
                    break;

                    case "HARDLINK":
                    hardlink = true;
                    break;

                    case "LINKINPLACE":
                    linkInPlace = true;
                    break;

                    case "MS":
                    case "MAXSIZE":
                    string argumentValue = m.Groups[2].ToString();
                    processSizeSuffix:
                    char ending = argumentValue.Last();
                    switch (ending) {
                        case '0':
                        case '1':
                        case '2':
                        case '3':
                        case '4':
                        case '5':
                        case '6':
                        case '7':
                        case '8':
                        case '9':
                        maxSize = int.Parse(argumentValue);
                        break;
                        case 'k':
                        case 'K':
                        maxSize = 1000 * int.Parse(string.Concat(argumentValue.SkipLast(1)));
                        break;
                        case 'm':
                        case 'M':
                        maxSize = 1000 * 1000 * int.Parse(string.Concat(argumentValue.SkipLast(1)));
                        break;
                        case 'g':
                        case 'G':
                        maxSize = 1000 * 1000 * 1000 * int.Parse(string.Concat(argumentValue.SkipLast(1)));
                        break;
                        case 'b':
                        case 'B':
                        argumentValue = string.Concat(argumentValue.SkipLast(1));
                        goto processSizeSuffix;
                    }
                    break;
                }
                //Console.WriteLine(m.Groups[1]+":"+m.Groups[2]);
            }
            if (folder is null) {
                if (target is string) {
                    FileInfo fi = new FileInfo(target);
                    target = fi.FullName;
                    folder = fi.DirectoryName;
                } else {
                    Console.WriteLine("fatal error: no target file or directory provided");
                    return 1;
                }
            }
            if (output is null) {
                output = Path.Combine(folder, "DeDuplicated");
            }

            var subDirectoryNames = Directory.GetDirectories(folder);
            var files = GetAllFilesInDirectory(folder, d => !Regex.IsMatch(d, "DeDuplicated$"))
            .Select(GetFileHash)
            .Where(f => f.File.Length < maxSize)
            .Where(f => !f.File.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .GroupBy(f => f.FileHash, new ByteArrayComparer())
            .ToList();

            files.RemoveAll(v => v.Count() <= 1);

            if (target != null) {
                files.RemoveAll(v => !ByteArrayComparer.StaticEquals(v.Key, GetFileHash(target).FileHash));
            }
            
            Dictionary<string, List<string>> duplicates = new Dictionary<string, List<string>>();
            Console.WriteLine("files with duplicates:");
            foreach (var item in files) {
                IOrderedEnumerable<(FileInfo File, byte[] FileHash)> sorted;
                if (target is null) {
                    sorted = item.OrderBy(v => v.File.Name.Length);
                } else {
                    sorted = item.OrderBy(v => v.File.FullName.Equals(target) ? int.MinValue : v.File.Name.Length);
                }
                var shortest = sorted.First();
                byte[] shortestBytes = File.ReadAllBytes(shortest.File.FullName);
                Console.WriteLine($"\t{shortest.File.FullName}:");
                sorted.Skip(1).ToList().ForEach(v => {
                    Console.Write($"\t\t{v.File.Name}");
                    if (ByteArrayComparer.EqualsWithSizeCheck(shortestBytes, File.ReadAllBytes(v.File.FullName))) {
                        Console.Write(" (true duplicate)");
                        duplicates.AddToMultiDict(shortest.File.FullName, v.File.FullName);
                    }
                    Console.WriteLine();
                });
                //sorted.Skip(1).ToList().ForEach(v => Console.WriteLine($"\t\t{v.File.FullName}"));
            }
            Console.WriteLine();
            if (fixMode) {
                if (linkInPlace) {
                    Func<string, string, bool> createLink = (location, target) => Linker.CreateSymbolicLink(location, target, 0);
                    if (hardlink) {
                        createLink = (location, target) => Linker.CreateHardLink(location, target, IntPtr.Zero);
                    }
                    foreach (var item in duplicates) {
                        //Console.WriteLine($"{item.Key}<-");
                        //item.Value.ForEach(v => Console.WriteLine(v));
                        foreach (string duplicate in item.Value) {
                            File.Delete(duplicate);
                            try {
                                if (!createLink(duplicate, item.Key)) throw new();
                                Console.WriteLine("created symlink " + duplicate);
                            } catch (Exception e) {
                                FileStream failedReplacement = File.Create(duplicate + "-sym");
                                failedReplacement.Write(File.ReadAllBytes(item.Key));
                                Console.WriteLine("fatal error while replacing files: "+e);
                                return 1;
                            }
                        }
                    }
                }
            }
            /*Console.WriteLine("files with duplicates:");
            foreach (var item in files.Where(v => v.ToList().Count > 1)) {
                var sorted = item.OrderBy(v => v.FileName.Length);
                Console.WriteLine($"\t{sorted.First().FileName}:");
                sorted.Skip(1).ToList().ForEach(v => Console.WriteLine($"\t\t{v.FileName}"));
            }
            Console.WriteLine("\nfiles without duplicates:");
            foreach (var item in files.Where(v => v.ToList().Count <= 1)) {
                Console.WriteLine($"\t+{item.First().FileName}");
            }*/
            //Console.WriteLine("folder: "+folder);
            //Console.WriteLine("output: "+output);
            //Console.WriteLine("target: "+target);
            return 0;
        }
        static (FileInfo File, byte[] FileHash) GetFileHash(string fileName) {
            using FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read);
            return (
                new FileInfo(fileName),
                SHA1.Create().ComputeHash(fs)
            );
        }
        public static IEnumerable<string> GetAllFilesInDirectory(string folder, Func<string, bool> filter) {
            return Directory.GetFiles(folder)
            .Union(Directory.GetDirectories(folder).Where(filter).SelectMany(GetAllFilesInDirectory));
        }
        public static IEnumerable<string> GetAllFilesInDirectory(string folder) {
            return Directory.GetFiles(folder)
            .Union(Directory.GetDirectories(folder).SelectMany(GetAllFilesInDirectory));
        }
        
		public static void AddToMultiDict<TKey, TValue, TCollection>(this Dictionary<TKey, TCollection> dictionary, TKey key, TValue value) where TCollection : ICollection<TValue>, new() {
			if(dictionary.ContainsKey(key)) {
				dictionary[key].Add(value);
			} else {
				dictionary.Add(key, new TCollection{value});
			}
		}
    }
    public class ByteArrayComparer : IEqualityComparer<byte[]> {
        public bool Equals(byte[] first, byte[] second) => StaticEquals(first, second);
        public static bool StaticEquals(byte[] first, byte[] second) {
            for (int i = 0; i < first.Length; i++) {
                if (first[i] != second[i])
                    return false;
            }
            return true;
        }
        public static bool EqualsWithSizeCheck(byte[] first, byte[] second) {
            if (first.Length != second.Length) {
                return false;
            }
            for (int i = 0; i < first.Length; i++) {
                if (first[i] != second[i])
                    return false;
            }
            return true;
        }
        
        public int GetHashCode([DisallowNull] byte[] obj) => StaticGetHashCode(obj);
        public static int StaticGetHashCode([DisallowNull] byte[] obj) => obj[0];
    }
    internal static class Linker {
        [DllImport("kernel32.dll")]
        internal static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, SymbolicLink dwFlags);

        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode )]
        internal static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        internal enum SymbolicLink {
            File = 0,
            Directory = 1
        }
    }
}
