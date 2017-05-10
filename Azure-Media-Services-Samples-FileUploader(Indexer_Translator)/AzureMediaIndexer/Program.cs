using System;
using System.Configuration;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Collections.Generic;
using System.Data.SqlClient;
using Microsoft.WindowsAzure.MediaServices.Client;

namespace AzureMediaIndexer
{
    class Program
    {

        private static string MicrosoftTranslatorKey = ConfigurationManager.AppSettings["MicrosoftTranslatorKey"];
        private static string from = ConfigurationManager.AppSettings["from"];
        private static string to = ConfigurationManager.AppSettings["to"];

        private static string storageAccountName = ConfigurationManager.AppSettings["storageAccountName"];
        private static string storageAccountKey = ConfigurationManager.AppSettings["storageAccountKey"];
        private static string IndexingConfigurationFile = ConfigurationManager.AppSettings["IndexingConfigurationFile"];
        private static string assetID = ConfigurationManager.AppSettings["AssetID"];

        private static string uploadFile = ConfigurationManager.AppSettings["uploadfile"];

        static void Main(string[] args)
        {

            string PlayerURL = string.Empty;
            string enVTTfile = string.Empty; // Central file for translation

            // for DB registration
            Dictionary<string, string> cc = new Dictionary<string, string>();

            // 処理時間の計測
            var totalSw = new Stopwatch();
            var sw = new Stopwatch();

            totalSw.Start();

            var context = new CloudMediaContext(
                    new MediaServicesCredentials(
                        ConfigurationManager.AppSettings["accountName"],
                        ConfigurationManager.AppSettings["accountKey"]
                    ));

            Console.WriteLine("*** 1. ファイルアップロード ***");
            sw.Start();

            IAsset asset;
            if (assetID.Length > 0)
            {
                asset = (from a in context.Assets
                         where a.Id.Equals(assetID)
                         select a).FirstOrDefault();
                if (asset is null) { throw new Exception("指定したAssetIDのファイルが見つかりません"); }

            }
            else
            {
                if (args.Length > 0) { uploadFile = args[0]; }
                if (!File.Exists(uploadFile)) { throw new ArgumentException($"指定したファイルがありません: {uploadFile}"); }

                asset = context.Assets.CreateFromFile(
                    uploadFile,
                    AssetCreationOptions.None,
                    (a, p) =>
                    {
                        Console.WriteLine("  経過 {0}%", p.Progress);
                    });
            }

            sw.Stop();
            Console.WriteLine("  アップロード処理完了");
            Console.WriteLine($"  アップロード処理時間: {sw.Elapsed.ToString()}");

            Console.WriteLine("*** 2.1. Encode 実行 (配信用) ***");
            sw.Reset();
            sw.Start();
            IAsset outputAsset;

            PlayerURL = MediaProcess(context,
                asset,
                "Media Encoder Standard",
                "Adaptive Streaming",
                out outputAsset);
            Console.WriteLine("  Encode 完了");
            Console.WriteLine($"  Encode 時間: { sw.Elapsed.ToString()}");

            Console.WriteLine("*** 2.2. Encode 実行 (Indexer 用) ***");
            sw.Reset();
            sw.Start();
            IAsset outputAssetForIndexer;

            PlayerURL = MediaProcess(context,
                asset,
                "Media Encoder Standard",
                "H264 Single Bitrate 16x9 SD",
                out outputAssetForIndexer);
            Console.WriteLine("  Encode 完了");
            Console.WriteLine($"  Encode 時間: { sw.Elapsed.ToString()}");


            Console.WriteLine("*** 3. Indexing 実行 ***");
            sw.Reset();
            sw.Start();

            var indexLang = "JaJp";
            switch (from)
            {
                case "en":
                    indexLang = "EnUs";
                    break;
                case "es":
                    indexLang = "EsEs";
                    break;
                case "zh-CHS":
                    indexLang = "ZhCn";
                    break;
                case "fr":
                    indexLang = "FrFr";
                    break;
                case "de":
                    indexLang = "DeDe";
                    break;
                case "it":
                    indexLang = "ItIt";
                    break;
                case "pt":
                    indexLang = "PtBr";
                    break;
                case "ar":
                    indexLang = "ArEg";
                    break;
                default:
                    break;
            }

            var indexConfigString = File.ReadAllText(IndexingConfigurationFile).Replace("{indexLang}", indexLang);

            var originalVTTURL = MediaProcess(context,
                outputAssetForIndexer,
                "Azure Media Indexer 2 Preview",
                indexConfigString,
                out outputAsset);
            Console.WriteLine("  Indexing 完了");
            Console.WriteLine($"  Indexing 時間: { sw.Elapsed.ToString()}");

            cc.Add(from, originalVTTURL);

            Console.WriteLine("*** 4. 字幕ファイルダウンロード (翻訳用) ***");
            sw.Reset();
            sw.Start();

            var originalVTTFile = string.Empty;
            foreach (var file in outputAsset.AssetFiles)
            {

                // WebVTT のみ翻訳対象 - Azure Media Player 用
                if (file.Name.EndsWith(".vtt"))
                {
                    Console.WriteLine($" ファイルダウンロード中: {file.Name}");

                    originalVTTFile = $@"{Environment.GetEnvironmentVariable("USERPROFILE")}\Desktop\{file.Name}".Replace(".", $"_{from}.");
                    file.Download(originalVTTFile);
                }

            }

            Console.WriteLine("*** 5. Microsoft Translator での 機械翻訳 ***");
            sw.Reset();
            sw.Start();

            var fromVTTfile = originalVTTFile;

            VTTTranslator translator = new VTTTranslator(MicrosoftTranslatorKey);
            translator.from = from;
            
            var langs = to.Split(',');
            foreach (var lang in langs)
            {
                var translatedVTTFile = fromVTTfile.Replace(
                        $"_{translator.from}.",
                        $"_{lang}.");

                translator.to = lang;
                translator.Translate(fromVTTfile, translatedVTTFile);
                Console.WriteLine($"  機械翻訳時間: {sw.Elapsed.ToString()}");
                Console.WriteLine("*** 6. 翻訳後のファイルを Azure Media Services に登録 ***");

                var TranslatedAsset = context.Assets.CreateFromFile(
                    translatedVTTFile,
                    AssetCreationOptions.None,
                    (a, p) =>
                    {
                        Console.WriteLine($"  経過 {p.Progress}%");
                    });

                context.Locators.CreateLocator(
                    LocatorType.OnDemandOrigin,
                    TranslatedAsset,
                    context.AccessPolicies.Create(
                    "Download Access Policy",
                    TimeSpan.FromDays(700),
                    AccessPermissions.Read)
                );

                cc.Add(lang, GetOnDemandStreamingURL(TranslatedAsset));

                if (lang == "en") {
                    translator.from = "en";
                    fromVTTfile = translatedVTTFile;
                }
            }

            string sqlInsertColumnName = string.Empty;
            string sqlInsertValue = string.Empty;

            Console.WriteLine($"PlayerURL: {PlayerURL}");
            foreach (var item in cc)
            {
                Console.WriteLine($"{item.Key}: {item.Value}");
                sqlInsertColumnName += $",[CC{item.Key.Replace("-","")}]";
                sqlInsertValue += $",N'{item.Value}'";

            }

            Console.WriteLine("*** 7. 再生情報をDatabaseに登録 ***");
            sw.Reset();
            sw.Start();
            var constr = ConfigurationManager.AppSettings["SQLConnection"];
            var result = 0;
            using (SqlConnection con = new SqlConnection(constr))
            {
                con.Open();
                SqlCommand sql = new SqlCommand($@"INSERT INTO [dbo].[VideoLiST]
                       ([FileName],
                       [Title],
                       [PlayerURL]
                        {sqlInsertColumnName})
                 VALUES
                       (N'{Path.GetFileNameWithoutExtension(uploadFile)}',
                        N'{Path.GetFileNameWithoutExtension(uploadFile)}',
                        N'{PlayerURL}'
                        {sqlInsertValue})", 
                       con);
                result = sql.ExecuteNonQuery();
            }

            Console.WriteLine("  再生情報をDatabaseに登録");
            Console.WriteLine($"  DB登録 時間: { sw.Elapsed.ToString()}");


            Console.WriteLine();
            Console.WriteLine("全ての処理が終了しました。");
            Console.WriteLine("総処理時間: {0}", totalSw.Elapsed.ToString());
            Console.WriteLine("何かキーを押してください。");
            Console.ReadLine();

        }


        private static string MediaProcess(
            CloudMediaContext context, 
            IAsset asset,
            string MPname,
            string config,
            out IAsset outputAsset)
        {

            var job = context.Jobs.CreateWithSingleTask(
                MPname,
                config,
                asset,
                $"{asset.Name}-out-{MPname}",
                AssetCreationOptions.None);

            job.Submit();
            job = job.StartExecutionProgressTask(
                j =>
                {
                    Console.WriteLine($"   状態: {j.State}");
                    Console.WriteLine($"   経過: {j.GetOverallProgress():0.##}%");
                },
                System.Threading.CancellationToken.None).Result;

            outputAsset = job.OutputMediaAssets.FirstOrDefault();
            context.Locators.CreateLocator(
                LocatorType.OnDemandOrigin,
                outputAsset,
                context.AccessPolicies.Create(
                "Download Access Policy",
                TimeSpan.FromDays(700),
                AccessPermissions.Read)
            );

            var returnURL = string.Empty;

            if (MPname == "Media Encoder Standard")
            {
                returnURL = outputAsset.GetSmoothStreamingUri().AbsoluteUri;

            } else
            {
                returnURL = GetOnDemandStreamingURL(outputAsset);
            }

            return returnURL;

        }



        private static string GetOnDemandStreamingURL(IAsset asset)
        {
            var locator = (from l in asset.Locators
                           where l.AssetId.Equals(asset.Id)
                           select l
                            ).FirstOrDefault();
            var uriBuilder = new UriBuilder(locator.Path);
            uriBuilder.Path += (from af in asset.AssetFiles
                                select af).FirstOrDefault().Name;
            return uriBuilder.Uri.AbsoluteUri;

        }

    }
}