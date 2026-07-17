using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Algorithm.ZipLineClustering;
using Algorithm.ZipLineClustering.ClusterTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLipsum.Core;

namespace Test.ZipLine
{
    [TestClass]
    public class ClusteringTests
    {
        [TestMethod]
        public void SampleClustersTest()
        {
            const int inputsCount = 1000;
            Clustering clustering = CreateClusteringEngine();

            foreach (string inputText in Enumerable.Range(0, inputsCount).Select(i => "This is sample input with some text added to fluff it up" + i))
            {
                clustering.AddItem(Guid.NewGuid().ToString(), inputText);
            }

            // resultingClusters is a list of clusters, where each cluster is a list of input IDs
            List<List<string>> resultingClusters = clustering.GetClustersItemIds();

            Assert.IsNotNull(resultingClusters);
            Assert.IsTrue(resultingClusters.Count > 0);
            Assert.AreEqual(inputsCount, resultingClusters.Sum(c => c.Count));
        }


        [TestMethod]
        public void LipsumClustersTest()
        {
            const int inputsCount = 1000;
            const int intendedClustersCount = 5;
            var rnd = new Random();

            Clustering clustering = CreateClusteringEngine();

            string rawText = Lipsums.LoremIpsum;
            var lipsum = new LipsumGenerator(rawText, false);

            string[] generatedSentences = lipsum.GenerateSentences(intendedClustersCount * 5, Sentence.Long);
            generatedSentences = generatedSentences.OrderByDescending(s => s.Length).Take(intendedClustersCount).ToArray();

            for (int i = 0; i < inputsCount; i++)
            {
                int intendedCluster = i % generatedSentences.Length;
                string sentence = generatedSentences[intendedCluster];
                // swap two words
                string[] words = sentence.Split(' ');
                int ix1 = rnd.Next(words.Length);
                int ix2 = rnd.Next(words.Length);
                string w1 = words[ix1];
                words[ix1] = words[ix2];
                words[ix2] = w1;

                sentence = string.Join(" ", words);

                clustering.AddItem($"{intendedCluster}-{i}:{ix1}:{ix2}", sentence);
            }

            List<List<string>> resultingClusters = clustering.GetClustersItemIds();

            Assert.IsNotNull(resultingClusters);
            Assert.IsTrue(resultingClusters.Count >= intendedClustersCount);
            foreach (List<string> cluster in resultingClusters)
            {
                IEnumerable<IGrouping<string, string>> desiredGroups = cluster.GroupBy(id => id.Split('-').First());
                Assert.AreEqual(1, desiredGroups.Count());
            }
        }

        [TestMethod]
        public void ClusteringSerializationRoundTripTest()
        {
            Clustering clustering = CreateClusteringEngine();
            clustering.AddItem("one", "One two three four five six seven");
            clustering.AddItem("two", "One two three four five six eight");

            string json = JsonSerializer.Serialize(clustering);
            Clustering restored = JsonSerializer.Deserialize<Clustering>(json);

            Assert.IsNotNull(restored);
            Assert.AreEqual(2, restored.GetClustersItemIds().Sum(cluster => cluster.Count));
        }

        [TestMethod]
        public void OrderedTokenSequenceHasHigherAffinityThanReorderedSequence()
        {
            var config = new ClusteringConfig { MinClusterAffinity = 0.85f };
            var vocabulary = new ClusteringVocabulary(config);
            const string orderedContent = "alpha beta gamma delta epsilon zeta eta theta";
            var orderedItem = new ClusterItem("ordered", orderedContent, vocabulary);
            var reorderedItem = new ClusterItem("reordered", "alpha gamma beta delta epsilon zeta eta theta", vocabulary);
            TokenZipNode root = JsonSerializer.Deserialize<TokenZipNode>(CreateZipChainJson(orderedItem.TokenIndex.Tokens.Select(token => token.Id)));
            var cluster = new ZipLineClusterProbe(config);

            Assert.IsNotNull(root);

            float orderedAffinity = cluster.GetZipAffinity(orderedItem, root);
            float reorderedAffinity = cluster.GetZipAffinity(reorderedItem, root);

            Assert.IsGreaterThan(reorderedAffinity, orderedAffinity);
        }

        [TestMethod]
        public void GeneratedHttpLogsClusterByMessageTemplate()
        {
            const int entriesPerTemplate = 40;
            Clustering clustering = CreateClusteringEngine();

            for (int index = 0; index < entriesPerTemplate; index++)
            {
                clustering.AddItem($"http-{index}", CreateHttpLog(index));
                clustering.AddItem($"auth-{index}", CreateAuthenticationLog(index));
                clustering.AddItem($"db-{index}", CreateDatabaseLog(index));
            }

            AssertTemplateClusters(clustering, entriesPerTemplate * 3, 3);
        }

        [TestMethod]
        public void GeneratedHttpLogsIgnoreVariableRequestData()
        {
            const int entriesCount = 60;
            Clustering clustering = CreateClusteringEngine();

            for (int index = 0; index < entriesCount; index++)
            {
                clustering.AddItem($"http-{index}", CreateHttpLog(index));
            }

            AssertTemplateClusters(clustering, entriesCount, 1);
        }

        [TestMethod]
        public void OtelDemoLogsClusterAndWriteClustersFile()
        {
            string samplePath = Path.Combine(AppContext.BaseDirectory, "SampleData", "otel-demo-logs.json");
            string clustersPath = Path.ChangeExtension(samplePath, ".clusters");
            var selectedEntries = new Dictionary<string, List<string>>();

            var clustering = CreateClusteringEngine(affinityThreshold: 0.88f);

            int inputObjectCount = 0;
            var stopwatch = Stopwatch.StartNew();

            var iterations = 1000;
            var dataSize = 0;

            for (int i = 0; i < iterations; i++)
            {
                foreach (JsonDocument document in ReadJsonObjects(samplePath))
                {
                    inputObjectCount++;
                    using (document)
                    {
                        string rawJson = document.RootElement.GetRawText();
                        dataSize += rawJson.Length;

                        foreach (JsonElement logRecord in GetLogRecords(document.RootElement))
                        {
                            if (!TryGetString(logRecord, "body", "stringValue", out string content)
                                || !TryGetString(logRecord, "traceId", out string traceId)
                                || !TryGetString(logRecord, "spanId", out string spanId))
                            {
                                continue;
                            }

                            var attributes = CollectAttributes(logRecord);
                            FilterAttributes(attributes);
                            content = ApplyAttributeReplacement(content, attributes);

                            var regexReplacements = new Dictionary<string, string>
                            {
                                //  ISO datetime replaced with "T"
                                [@"\[\d{4}-\d{2}-\d{2}T\d+:\d+:\d+\.\d+Z\]"] = "T",
                                // GUID replaced with "GUID"
                                [@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"] = "GUID",
                                // HTTP status code xxx replaced with "HTTPxxx"
                                [@"HTTP\s?/\s?1\.1..?.?([2345]\d{2})\b"] = "HTTP$1",
                                // IPv4 address with port replaced with "IPvFOUR:PORT"
                                [@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}:\d+\b"] = "IPvFOUR:PORT",
                                // IPv4 address replaced with "IPvFOUR"
                                [@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b"] = "IPvFOUR",
                                // IPv6 address replaced with "IPvSIX"
                                [@"\b(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b"] = "IPvSIX",
                                // Series of digits replaced with "NUM"
                                [@"\b\d+\b"] = "NUM"
                            };

                            content = ApplyRegexReplacements(content, regexReplacements);
                            var contentID = traceId + "-" + spanId;

                            if (!selectedEntries.TryGetValue(contentID, out List<string> sourceObjects))
                            {
                                sourceObjects = new List<string>();
                                selectedEntries.Add(contentID, sourceObjects);
                                clustering.AddItem(contentID, content);
                            }

                            sourceObjects.Add(content);
                        }
                    }
                }
            }

            List<List<string>> clusters = clustering.GetClustersItemIds();
            stopwatch.Stop();

            using (var writer = new StreamWriter(clustersPath, false))
            {
                for (int clusterId = 0; clusterId < clusters.Count; clusterId++)
                {
                    writer.WriteLine(clusterId);
                    foreach (string contentID in clusters[clusterId])
                    {
                        foreach (string sourceObject in selectedEntries[contentID])
                        {
                            writer.WriteLine(sourceObject);
                        }
                    }
                }
            }

            int[] clusterSizes = clusters.Select(cluster => cluster.Count).ToArray();
            int selectedRecordCount = selectedEntries.Sum(entry => entry.Value.Count);
            Console.WriteLine($"OTEL clustering processed {inputObjectCount} JSON objects and selected {selectedRecordCount} log records ({selectedEntries.Count} unique span IDs), data size: {dataSize} bytes, in {stopwatch.ElapsedMilliseconds} ms.");
            Console.WriteLine($"Created {clusters.Count} clusters; min size: {clusterSizes.Min()}, max size: {clusterSizes.Max()}, average size: {clusterSizes.Average():F2}.");
            Console.WriteLine($"Clusters written to {clustersPath}");

            Assert.IsTrue(File.Exists(clustersPath));
            Assert.IsTrue(selectedEntries.Count > 0);
            Assert.AreEqual(selectedEntries.Count, clusters.Sum(cluster => cluster.Count));
        }

        private string ApplyRegexReplacements(string content, Dictionary<string, string> regexReplacements)
        {
            foreach (var kv in regexReplacements)
            {
                content = Regex.Replace(content, kv.Key, kv.Value);
            }

            return content;
        }

        private string ApplyAttributeReplacement(string content, Dictionary<string, string> attributes)
        {
            var kvByLen = attributes.OrderByDescending(kv => kv.Value.Length).ToList();
            foreach (var kv in kvByLen)
            {
                if (content.Length > kv.Value.Length + 2)
                {
                    content = content.Replace(kv.Value, $"{{{kv.Key}}}");
                }
            }

            return content;
        }

        private static void FilterAttributes(Dictionary<string, string> attributes)
        {
            var removeKeys = new List<string>();
            foreach (var key in attributes.Keys)
            {
                var val = attributes[key];
                if (string.IsNullOrEmpty(val))
                {
                    removeKeys.Add(key);
                }
                else if (val.Length <= 4 || attributes.ContainsKey(val))
                {
                    removeKeys.Add(key);
                }
            }

            foreach (var key in removeKeys)
            {
                attributes.Remove(key);
            }
        }


        ///
        /// Recursively collects string attributes from a JSON log record into a dictionary.
        /// </summary>
        /// <param name="jsonEl">The JSON element to collect attributes from.</param>
        /// <param name="attributes">An optional dictionary to populate with attributes.</param>
        /// <returns>A dictionary containing the collected string attributes.</returns>
        private Dictionary<string, string> CollectAttributes(JsonElement jsonEl, Dictionary<string, string> attributes = null)
        {
            attributes ??= new Dictionary<string, string>();

            if (jsonEl.TryGetProperty("key", out JsonElement keyEl1)
                && jsonEl.TryGetProperty("value", out JsonElement valueEl1)
                && valueEl1.TryGetProperty("stringValue", out JsonElement stringValueEl1))
            {
                attributes[keyEl1.GetString()] = stringValueEl1.GetString();
                return attributes;
            }

            // collect the props from the json element, then collect recursively from nested objects
            foreach (JsonProperty property in jsonEl.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    attributes[property.Name] = property.Value.GetString();
                }
                else if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    // special treatment for protobuf structure from OTEL:
                    // {key:"keyname", value: { stringValue: "attrValue" }}
                    // collect keyname=attrValue
                    if (property.Value.TryGetProperty("key", out JsonElement keyEl)
                        && property.Value.TryGetProperty("value", out JsonElement valueEl)
                        && valueEl.TryGetProperty("stringValue", out JsonElement stringValueEl))
                    {
                        attributes[keyEl.GetString()] = stringValueEl.GetString();
                    }
                    else
                    {
                        CollectAttributes(property.Value, attributes);
                    }
                }
                else if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            CollectAttributes(item, attributes);
                        }
                    }
                }
            }

            return attributes;
        }

        [TestMethod]
        public void GeneratedFailureLogsDoNotMixWithSuccessLogs()
        {
            const int entriesPerTemplate = 35;
            Clustering clustering = CreateClusteringEngine();

            for (int index = 0; index < entriesPerTemplate; index++)
            {
                clustering.AddItem($"success-{index}", CreateHttpLog(index));
                clustering.AddItem($"failure-{index}", CreateHttpFailureLog(index));
            }

            AssertTemplateClusters(clustering, entriesPerTemplate * 2, 2);
        }

        [TestMethod]
        public void AdversarialCriticalActionTokensKeepNearIdenticalLogsSeparate()
        {
            const int entriesPerTemplate = 30;
            Clustering clustering = CreateClusteringEngine(new Dictionary<string, float>
            {
                { "granted|revoked", 10 }
            });

            for (int index = 0; index < entriesPerTemplate; index++)
            {
                clustering.AddItem($"grant-{index}", $"INFO authorization permission granted principal user-{index} role billing_admin tenant tenant-{index % 5} request auth-{index:X8}");
                clustering.AddItem($"revoke-{index}", $"INFO authorization permission revoked principal user-{index} role billing_admin tenant tenant-{index % 5} request auth-{index:X8}");
            }

            AssertTemplateClusters(clustering, entriesPerTemplate * 2, 2);
        }

        [TestMethod]
        public void AdversarialReorderedFieldsCanMergeTemplates()
        {
            const int entriesPerTemplate = 30;
            Clustering clustering = CreateClusteringEngine();

            for (int index = 0; index < entriesPerTemplate; index++)
            {
                clustering.AddItem($"forward-{index}", $"INFO worker task started queue invoices priority high tenant tenant-{index % 4} request job-{index:X8}");
                clustering.AddItem($"reordered-{index}", $"INFO worker task started priority high queue invoices tenant tenant-{index % 4} request job-{index:X8}");
            }

            AssertTemplateMixing(clustering, entriesPerTemplate * 2);
        }

        [TestMethod]
        public void AdversarialTerseTemplatesWithSharedVocabularyCanMix()
        {
            const int entriesPerTemplate = 30;
            Clustering clustering = CreateClusteringEngine();

            for (int index = 0; index < entriesPerTemplate; index++)
            {
                clustering.AddItem($"created-{index}", $"WARN cache entry created key item-{index:X8} region east");
                clustering.AddItem($"expired-{index}", $"WARN cache entry expired key item-{index:X8} region east");
            }

            AssertTemplateMixing(clustering, entriesPerTemplate * 2);
        }

        private static string CreateHttpLog(int index)
        {
            return $"2026-07-16T10:{index % 60:D2}:00Z INFO gateway request completed method GET route /orders/{100000 + index} status 200 duration {20 + index % 180}ms request req-{index:X8} user user-{index % 17} ip 10.42.{index % 255}.{(index * 7) % 255}";
        }

        private static string CreateHttpFailureLog(int index)
        {
            return $"2026-07-16T11:{index % 60:D2}:00Z ERROR gateway request failed method GET route /orders/{100000 + index} status 500 error timeout request req-{index:X8} user user-{index % 17} ip 10.43.{index % 255}.{(index * 11) % 255}";
        }

        private static string CreateAuthenticationLog(int index)
        {
            return $"2026-07-16T12:{index % 60:D2}:00Z WARN identity login rejected provider oidc tenant tenant-{index % 9} user user-{index} reason invalid_password request auth-{index:X8} ip 10.44.{index % 255}.{(index * 13) % 255}";
        }

        private static string CreateDatabaseLog(int index)
        {
            return $"2026-07-16T13:{index % 60:D2}:00Z INFO inventory database query completed operation select table products shard shard-{index % 5} rows {1 + index % 100} duration {5 + index % 80}ms request db-{index:X8}";
        }

        private static void AssertTemplateClusters(Clustering clustering, int expectedItemCount, int expectedTemplateCount)
        {
            List<List<string>> clusters = clustering.GetClustersItemIds();

            Assert.AreEqual(expectedItemCount, clusters.Sum(cluster => cluster.Count));
            Assert.IsTrue(clusters.Count >= expectedTemplateCount);
            foreach (List<string> cluster in clusters)
            {
                Assert.AreEqual(1, cluster.Select(id => id.Split('-').First()).Distinct().Count());
            }
        }

        private static void AssertTemplateMixing(Clustering clustering, int expectedItemCount)
        {
            List<List<string>> clusters = clustering.GetClustersItemIds();

            Assert.AreEqual(expectedItemCount, clusters.Sum(cluster => cluster.Count));
            Assert.IsTrue(clusters.Any(cluster => cluster.Select(id => id.Split('-').First()).Distinct().Count() > 1));
        }

        private static List<JsonDocument> ReadJsonObjects(string path)
        {
            byte[] json = File.ReadAllBytes(path);
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowMultipleValues = true });
            var documents = new List<JsonDocument>();

            while (reader.Read())
            {
                documents.Add(JsonDocument.ParseValue(ref reader));
            }

            return documents;
        }

        private static IEnumerable<JsonElement> GetLogRecords(JsonElement root)
        {
            if (!root.TryGetProperty("resourceLogs", out JsonElement resourceLogs))
            {
                yield break;
            }

            foreach (JsonElement resourceLog in resourceLogs.EnumerateArray())
            {
                if (!resourceLog.TryGetProperty("scopeLogs", out JsonElement scopeLogs))
                {
                    continue;
                }

                foreach (JsonElement scopeLog in scopeLogs.EnumerateArray())
                {
                    if (!scopeLog.TryGetProperty("logRecords", out JsonElement logRecords))
                    {
                        continue;
                    }

                    foreach (JsonElement logRecord in logRecords.EnumerateArray())
                    {
                        yield return logRecord;
                    }
                }
            }
        }

        private static bool TryGetString(JsonElement element, string propertyName, out string value)
        {
            value = null;
            return element.TryGetProperty(propertyName, out JsonElement property)
                && property.ValueKind == JsonValueKind.String
                && (value = property.GetString()) != null;
        }

        private static bool TryGetString(JsonElement element, string parentPropertyName, string propertyName, out string value)
        {
            value = null;
            return element.TryGetProperty(parentPropertyName, out JsonElement parent)
                && TryGetString(parent, propertyName, out value);
        }

        private static string CreateZipChainJson(IEnumerable<int> tokenIds)
        {
            string nodeJson = null;
            foreach (int tokenId in tokenIds.Reverse())
            {
                nodeJson = nodeJson == null
                    ? $"{{\"t\":{tokenId}}}"
                    : $"{{\"t\":{tokenId},\"c\":[{nodeJson}]}}";
            }

            return $"{{\"t\":{TokenZipNode.WildcardId},\"c\":[{nodeJson}]}}";
        }

        private sealed class ZipLineClusterProbe : ZipLineCluster
        {
            public ZipLineClusterProbe(ClusteringConfig config) : base(config)
            {
            }

            public float GetZipAffinity(ClusterItem item, TokenZipNode root)
            {
                Tuple<float, float> matchingAndTotalWeight = this.CalculateAffinity(item, root, this.Config, 1);
                return matchingAndTotalWeight.Item1 / matchingAndTotalWeight.Item2;
            }
        }

        private static Clustering CreateClusteringEngine(Dictionary<string, float> weightedTokens = null, float affinityThreshold = 0.85f, bool logDebug = false)
        {
            var config = new ClusteringConfig
            {
                LogDebug = logDebug,
                MinClusterAffinity = affinityThreshold,
                WeightedTokens = weightedTokens ?? new Dictionary<string, float>
                {
                    {"Important Fragment Regex Here", 10}
                }
            };

            return new Clustering(config);
        }
    }
}
