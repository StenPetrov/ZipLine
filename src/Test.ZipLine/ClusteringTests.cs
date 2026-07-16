using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Algorithm.ZipLineClustering;
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

        private static Clustering CreateClusteringEngine()
        {
            var config = new ClusteringConfig
            {
                MinClusterAffinity = 0.85f,
                WeightedTokens = new Dictionary<string, float>
                {
                    {"Important Fragment Regex Here", 10}
                }
            };

            return new Clustering(config);
        }
    }
}
