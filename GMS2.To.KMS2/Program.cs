using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using GMS2.To.KMS2;
using Maple2.File.Parser.Tools;
using Maple2.File.Parser.Xml.Table.Server;

string clientTableFolder = Path.Combine(Paths.GMS2_DIR, "table");
string[] clientXmlFiles = Directory.GetFiles(clientTableFolder, "individualitemdrop*.xml", SearchOption.AllDirectories);
// remove individualitemdrop_monster
clientXmlFiles = clientXmlFiles.Where(x => !x.Contains("monster")).ToArray();

List<Maple2.File.Parser.Xml.Table.IndividualItemDropRoot> individualItemDropRoots = [];
foreach (string xmlPath in clientXmlFiles) {
    Maple2.File.Parser.Xml.Table.IndividualItemDropRoot? clientData = DeserializeXml<Maple2.File.Parser.Xml.Table.IndividualItemDropRoot>(xmlPath);
    if (clientData == null) {
        throw new Exception("Failed to deserialize xml");
    }
    individualItemDropRoots.Add(clientData);
}

IEnumerable<KeyValuePair<GroupKey, List<Maple2.File.Parser.Xml.Table.IndividualItemDrop>>> clientIndividualDropBoxesGrouped = individualItemDropRoots
    .SelectMany(data => data.__individualDropBox)
    // .Where(dropbox => dropbox.Locale == "NA" || dropbox.Locale == "")
    .GroupBy(dropbox => new GroupKey(dropbox.individualDropBoxID, dropbox.dropGroup))
    .ToDictionary(group => group.Key, group => group.ToList())
    .Where(dict => dict.Value.Count > 0);

List<IndividualItemDrop> clientIndividualDropBoxes = ConvertToServerIndividualItemDrop(clientIndividualDropBoxesGrouped).ToList();

string serverXmlFile = Path.Combine(Paths.SERVER_DIR, "table", "server", "individualItemDrop_Final_Backup.xml");

IndividualItemDropRoot? serverData = DeserializeXml<IndividualItemDropRoot>(serverXmlFile);
if (serverData == null) {
    throw new Exception("Failed to deserialize xml");
}

List<IndividualItemDrop> serverIndividualDropBoxes = serverData.dropBox;

// merge client and server data
foreach (IndividualItemDrop clientDropBox in clientIndividualDropBoxes) {
    IndividualItemDrop? serverDropBox = serverIndividualDropBoxes.FirstOrDefault(x => x.dropBoxID == clientDropBox.dropBoxID);
    if (serverDropBox == null) {
        serverIndividualDropBoxes.Add(clientDropBox);
        continue;
    }

    IndividualItemDrop.Group? clientGroup = clientDropBox.__group.First();
    IndividualItemDrop.Group? serverGroup = serverDropBox.__group.FirstOrDefault(x => x.dropGroupID == clientGroup.dropGroupID);
    if (serverGroup == null) {
        serverDropBox.__group.Add(clientGroup);
        continue;
    }

    foreach (IndividualItemDrop.Group.Item? clientItem in clientGroup.__v) {
        IndividualItemDrop.Group.Item? serverItem = serverGroup.__v.FirstOrDefault(x => x.itemID == clientItem.itemID);
        if (serverItem is not null) {
            // fix server item minCount
            if (serverItem.minCount == 0) {
                serverItem.minCount = 1;
            }

            // fix server item grade
            if (serverItem.grade.Length == 0) {
                serverItem.grade = [.. clientItem.grade];
            }
        }

        // if item exists in server, with same minCount and no locale, its good to go, skip
        if (serverItem is not null && serverItem.minCount == clientItem.minCount && serverItem.Locale == "") {
            // use weights from server
            clientItem.weight = serverItem.weight;
            clientItem.properJobWeight = serverItem.properJobWeight;
            clientItem.imProperJobWeight = serverItem.imProperJobWeight;
            continue;
        }

        if (serverItem is not null && serverItem._locale == "KR") {
            clientItem._locale = "NA";
            clientItem.weight = serverItem.weight;
            clientItem.properJobWeight = serverItem.properJobWeight;
            clientItem.imProperJobWeight = serverItem.imProperJobWeight;
            serverGroup.__v.Add(clientItem);
            continue;
        }

        if (serverItem is null) {
            serverGroup.__v.Add(clientItem);
        }
    }

    // if group has multiple items with different locales, set the group locale to empty
    if (serverGroup.__v.Select(x => x._locale).Distinct().Count() > 1) {
        serverGroup._locale = "";
    }
}

// order by dropBoxID, dropGroupID, itemID
serverIndividualDropBoxes = [.. serverIndividualDropBoxes.OrderBy(x => x.dropBoxID)];

foreach (IndividualItemDrop dropBox in serverIndividualDropBoxes) {
    dropBox.__group = [.. dropBox.__group.OrderBy(x => x.dropGroupID)];
    foreach (IndividualItemDrop.Group? group in dropBox.__group) {
        // group.__v = [.. group.__v.OrderBy(x => x.itemID)];
    }
}

// //split serverIndividualDropBoxes into groups of 1000
// int groupSize = 1000;
// int groupCount = (int) Math.Ceiling((double) serverIndividualDropBoxes.Count / groupSize);

// for (int i = 0; i < groupCount; i++) {
//     int start = i * groupSize;
//     int end = Math.Min((i + 1) * groupSize, serverIndividualDropBoxes.Count);
//     var group = serverIndividualDropBoxes.GetRange(start, end - start);

//     Maple2.File.Parser.Xml.Table.Server.IndividualItemDropRoot individualItemDropRoot = new Maple2.File.Parser.Xml.Table.Server.IndividualItemDropRoot() {
//         dropBox = group
//     };

//     XmlSerializer groupsSerializer = new XmlSerializer(typeof(Maple2.File.Parser.Xml.Table.Server.IndividualItemDropRoot));
//     string outputXmlPath = Path.Combine(Paths.SOLUTION_DIR, "individualItemDrop_Final_" + i + ".xml");

//     using XmlWriter writer = XmlWriter.Create(outputXmlPath, new XmlWriterSettings { Indent = true });
//     groupsSerializer.Serialize(writer, individualItemDropRoot);

//     Console.WriteLine("Groups saved to file: " + outputXmlPath);
// }

var individualItemDropRoot = new IndividualItemDropRoot() {
    dropBox = serverIndividualDropBoxes,
};

var groupsSerializer = new XmlSerializer(typeof(IndividualItemDropRoot));
string outputXmlPath = Path.Combine(Paths.SOLUTION_DIR, "server", "table", "server", "individualItemDrop_Final.xml");
using var writer = XmlWriter.Create(outputXmlPath, new XmlWriterSettings {
    Indent = true,
});
groupsSerializer.Serialize(writer, individualItemDropRoot);
writer.Close();

Console.WriteLine("Saved to file: " + outputXmlPath);

// Cleanup XML
string xmlString = File.ReadAllText(outputXmlPath);
xmlString = Regex.Replace(xmlString, " \\w+=\"\"", string.Empty); // remove empty attributes
xmlString = Regex.Replace(xmlString, " minCount=\"1\" maxCount=\"1\"", string.Empty); // remove minCount="1" maxCount="1"
string[] attributesToConvert = ["isBindCharacter", "disableBreak", "tradableCountDeduction", "rePackingLimitCountDeduction", "isApplySmartGenderDrop", "assistBonus", "constraintsQuest", "isAnnounce", "serverDrop"];
foreach (string attribute in attributesToConvert) {
    xmlString = Regex.Replace(xmlString, $" {attribute}=\"true\"", $" {attribute}=\"1\"");
}
File.WriteAllText(outputXmlPath, xmlString);


#region Methods
static T? DeserializeXml<T>(string xmlPath) where T : class {
    string xmlString = File.ReadAllText(xmlPath);
    xmlString = Sanitizer.RemoveEmpty(xmlString);
    xmlString = Sanitizer.SanitizeBool(xmlString);

    var xml = XmlReader.Create(new StringReader(xmlString));
    var individualItemDropSerializer = new XmlSerializer(typeof(T));
    var data = individualItemDropSerializer.Deserialize(xml) as T;
    return data;
}

IEnumerable<IndividualItemDrop> ConvertToServerIndividualItemDrop(IEnumerable<KeyValuePair<GroupKey, List<Maple2.File.Parser.Xml.Table.IndividualItemDrop>>> individualDropBoxesGrouped) {
    foreach (KeyValuePair<GroupKey, List<Maple2.File.Parser.Xml.Table.IndividualItemDrop>> dropbox in individualDropBoxesGrouped) {
        var serverDropBox = new IndividualItemDrop {
            dropBoxID = dropbox.Key.IndividualDropBoxID,
            __group = [
                new IndividualItemDrop.Group {
                    dropGroupID = dropbox.Key.DropGroup,
                    smartDropRate = dropbox.Value.First().smartDropRate,
                    dropGroupMinLevel = dropbox.Value.First().dropGroupMinLevel,
                    dropCount = dropbox.Value.First().dropCount,
                    dropCountProbability = dropbox.Value.First().dropCountProbability,
                    serverDrop = dropbox.Value.First().serverDrop,
                    isApplySmartGenderDrop = dropbox.Value.First().isApplySmartGenderDrop,
                    __v = dropbox.Value.Select(x => new Maple2.File.Parser.Xml.Table.Server.IndividualItemDrop.Group.Item() {
                        itemID = x.item,
                        itemID2 = x.item2,
                        isAnnounce = x.isAnnounce,
                        properJobWeight = x.properJobWeight,
                        imProperJobWeight = x.imProperJobWeight,
                        weight = x.weight,
                        assistBonus = x.assistBonus,
                        uiItemRank = (short) (x.PackageUIShowGrade ?? 0),
                        gradeProbability = x.gradeProbability,
                        grade = x.grade.Length > 0 ? x.grade.ToList().Select(y => (short) y).ToArray() : x.PackageUIShowGrade == null ? [] : [(short) x.PackageUIShowGrade],
                        enchantLevel = x.enchantLevel,
                        socketDataID = x.socketDataID,
                        tradableCountDeduction = x.tradableCountDeduction,
                        rePackingLimitCountDeduction = x.rePackingLimitCountDeduction,
                        isBindCharacter = x.isBindCharacter,
                        showTooltip = x.showTooltip,
                        disableBreak = x.disableBreak,
                        showIconOnGachaUI = x.showIconOnGachaUI,
                        mapDependency = x.mapDependency,
                        constraintsQuest = x.constraintsQuest,
                        minCount = (int) x.minCount,
                        maxCount = (int) x.maxCount,
                        _locale = x.Locale,
                    }).ToList(),
                },
            ],
        };

        yield return serverDropBox;
    }
}

internal record GroupKey(int IndividualDropBoxID, byte DropGroup);
#endregion
