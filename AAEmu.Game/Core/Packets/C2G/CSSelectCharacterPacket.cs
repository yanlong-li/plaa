using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AAEmu.Commons.Network;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Mails;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units.Route;

namespace AAEmu.Game.Core.Packets.C2G;

public class CSSelectCharacterPacket : GamePacket
{
    public CSSelectCharacterPacket() : base(CSOffsets.CSSelectCharacterPacket, 1)
    {
    }

    public override void Read(PacketStream stream)
    {
        var characterId = stream.ReadUInt32();
        var gm = stream.ReadBoolean();
        stream.ReadByte();

        if (Connection.Characters.TryGetValue(characterId, out var connectionCharacter))
        {
            // Despawn any old pets this character might have even before loading it
            var character = Connection.Characters[characterId];
            character.Load();
            character.Connection = Connection;
            var houses = Connection.Houses.Values.Where(x => x.OwnerId == character.Id);
            MateManager.Instance.RemoveAndDespawnAllActiveOwnedMates(character);

            Connection.ActiveChar = character;
            if (Character.UsedCharacterObjIds.TryGetValue(character.Id, out var oldObjId))
            {
                Connection.ActiveChar.ObjId = oldObjId;
            }
            else
            {
                Connection.ActiveChar.ObjId = ObjectIdManager.Instance.GetNextId();
                Character.UsedCharacterObjIds.TryAdd(character.Id, character.ObjId);
            }

            var mySlave = SlaveManager.Instance.GetActiveSlaveByOwnerObjId(Connection.ActiveChar.ObjId);
            if (mySlave != null)
            {
                Logger.Warn($"{Connection.ActiveChar.Name}: Прерываем задачу отключения транспорта");
                mySlave.CancelTokenSource.Cancel();
            }

            Connection.ActiveChar.Simulation = new Simulation(character);

            Connection.SendPacket(new SCCharacterStatePacket(character));
            Connection.SendPacket(new SCCharacterGamePointsPacket(character));
            Connection.ActiveChar.Inventory.Send();
            Connection.SendPacket(new SCActionSlotsPacket(Connection.ActiveChar.Slots));

            Connection.ActiveChar.Quests.Send();
            Connection.ActiveChar.Quests.SendCompleted();

            Connection.ActiveChar.Actability.Send();
            Connection.ActiveChar.Mails.SendUnreadMailCount();
            Connection.ActiveChar.Appellations.Send();
            Connection.ActiveChar.Portals.Send();
            Connection.ActiveChar.Friends.Send();
            Connection.ActiveChar.Blocked.Send();

            foreach (var house in houses)
            {
                house.CurrentStep = -1;
                house.ProtectionEndDate = DateTime.UtcNow.AddDays(90);
                Connection.SendPacket(new SCMyHousePacket(house));
            }

            foreach (var conflict in ZoneManager.Instance.GetConflicts())
            {
                Connection.SendPacket(new SCConflictZoneStatePacket(conflict.ZoneGroupId, conflict.CurrentZoneState,
                    conflict.NextStateTime));
            }

            FactionManager.Instance.SendFactions(Connection.ActiveChar);
            FactionManager.Instance.SendRelations(Connection.ActiveChar);
            ExpeditionManager.Instance.SendExpeditions(Connection.ActiveChar);

            if (Connection.ActiveChar.Expedition != null)
            {
                ExpeditionManager.SendExpeditionInfo(Connection.ActiveChar);
            }

            Connection.ActiveChar.SendOption(1);
            Connection.ActiveChar.SendOption(2);
            Connection.ActiveChar.SendOption(5);

            Connection.ActiveChar.Buffs.AddBuff((uint)BuffConstants.LoggedOn, Connection.ActiveChar);

            var template = CharacterManager.Instance.GetTemplate(character.Race, character.Gender);

            foreach (var buff in template.Buffs)
            {
                var buffTemplate = SkillManager.Instance.GetBuffTemplate(buff);
                var casterObj = new SkillCasterUnit(character.ObjId);
                character.Buffs.AddBuff(new Buff(character, character, casterObj, buffTemplate, null, DateTime.UtcNow) { Passive = true });
            }

            character.Breath = character.LungCapacity;

            Connection.ActiveChar.OnZoneChange(0, Connection.ActiveChar.Transform.ZoneId);

            var cts = new CancellationTokenSource();

            Connection.ActiveChar.PushSubscriber(cts);

            Task.Run(async () =>
            {
                var i = 0;
                while (!cts.IsCancellationRequested)
                {
                    await Task.Delay(60 * 60 * 1000, cts.Token);
                    i++;
                    var mail = new BaseMail();
                    mail.MailType = MailType.Admin;
                    mail.Title = "在线活动";
                    mail.ReceiverName = character.Name;
                    mail.Header.SenderId = 0;
                    mail.Header.SenderName = "GM";
                    mail.Header.ReceiverId = character.Id;
                    mail.Header.Extra = 0;
                    mail.Body.Text = $"您已累计在线 {i} 小时";
                    mail.Body.SendDate = DateTime.UtcNow;
                    mail.Body.RecvDate = DateTime.UtcNow;
                    mail.Body.CopperCoins = 50000 * i;
                    mail.Body.BillingAmount = 0;
                    var newItem = ItemManager.Instance.Create(23633, i, (byte)0);
                    newItem.OwnerId = character.Id;
                    newItem.SlotType = SlotType.Mail;
                    mail.Body.Attachments.Add(newItem);
                    mail.Send();
                }
            }, cts.Token);
        }
        else
        {
            // TODO ...
        }
    }
}
