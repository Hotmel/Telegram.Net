﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TLSharp.Core.Auth;
using TLSharp.Core.MTProto;
using TLSharp.Core.MTProto.Crypto;
using TLSharp.Core.Network;
using TLSharp.Core.Requests;
using MD5 = System.Security.Cryptography.MD5;

namespace TLSharp.Core
{
    public class TelegramClient
    {
        private MtProtoSender _protoSender;
        private AuthKey _key;
        private TcpTransport _transport;
        private readonly string _apiHash;
        private readonly int _apiId;
        private readonly Session _session;
        private List<DcOption> _dcOptions;

        public event EventHandler<Updates> UpdateMessage;

        public enum VerificationCodeDeliveryType
        {
            NumericCodeViaSms = 0,
            NumericCodeViaTelegram = 5
        }

        public TelegramClient(ISessionStore store, string sessionUserId, int apiId, string apiHash)
        {
            if (apiId == 0)
                throw new InvalidOperationException("Your API_ID is invalid. Do a configuration first https://github.com/sochix/TLSharp#quick-configuration");

            if (string.IsNullOrEmpty(apiHash))
                throw new InvalidOperationException("Your API_ID is invalid. Do a configuration first https://github.com/sochix/TLSharp#quick-configuration");

            _apiHash = apiHash;
            _apiId = apiId;
            _session = Session.TryLoadOrCreateNew(store, sessionUserId);
            _transport = new TcpTransport(_session.ServerAddress, _session.Port);
        }

        public async Task Connect(bool reconnect = false)
        {
            if (_session.AuthKey == null || reconnect)
            {
                var result = await Authenticator.DoAuthentication(_transport);
                _session.AuthKey = result.AuthKey;
                _session.TimeOffset = result.TimeOffset;
            }

            _protoSender = new MtProtoSender(_transport, _session);
            _protoSender.UpdateMessage += OnUpdateMessage;

            if (!reconnect)
            {
                var request = new InitConnectionRequest(_apiId);
                await _protoSender.Send(request);

                _dcOptions = request.ConfigConstructor.dc_options;
            }
        }

        private async Task ReconnectToDc(int dcId)
        {
            Debug.WriteLine("Switching");
            if (_dcOptions == null || !_dcOptions.Any())
                throw new InvalidOperationException("Can't reconnect. Establish initial connection first.");

            var dc = _dcOptions.Cast<DcOptionConstructor>().First(d => d.id == dcId);

            await CloseCurrentTransport();

            //var oldTransport = _transport;

            _transport = new TcpTransport(dc.ip_address, dc.port);
            _session.ServerAddress = dc.ip_address;
            _session.Port = dc.port;

            await Connect(true);
            Debug.WriteLine("Switched");
            //oldTransport?.Dispose();
        }

        private async Task CloseCurrentTransport()
        {
            _transport.Disconnect();

            await _protoSender.FinishedListeningTask;
            _protoSender.UpdateMessage -= OnUpdateMessage;

            _transport.Dispose();

            _transport = null;
        }

        public bool IsUserAuthorized()
        {
            return _session.User != null;
        }

        public async Task<bool> IsPhoneRegistered(string phoneNumber)
        {
            var authCheckPhoneRequest = new AuthCheckPhoneRequest(phoneNumber);
            await _protoSender.Send(authCheckPhoneRequest);
            //await _sender.Receive(authCheckPhoneRequest);

            return authCheckPhoneRequest._phoneRegistered;
        }

        public async Task<string> SendCodeRequest(string phoneNumber, VerificationCodeDeliveryType tokenDestination = VerificationCodeDeliveryType.NumericCodeViaTelegram)
        {
            var completed = false;

            AuthSendCodeRequest request = null;

            while (!completed)
            {
                request = new AuthSendCodeRequest(phoneNumber, (int)tokenDestination, _apiId, _apiHash, "en");
                try
                {
                    await _protoSender.Send(request);

                    completed = true;
                }
                catch (InvalidOperationException ex)
                {
                    if (ex.Message.StartsWith("Your phone number registered to") && ex.Data["dcId"] != null)
                    {
                        await ReconnectToDc((int)ex.Data["dcId"]);
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            return request._phoneCodeHash;
        }

        public async Task<User> MakeAuth(string phoneNumber, string phoneCodeHash, string code)
        {
            var request = new AuthSignInRequest(phoneNumber, phoneCodeHash, code);
            await _protoSender.Send(request);

            OnUserAuthenticated(request.user, request.SessionExpires);

            return request.user;
        }

        public async Task<User> SignUp(string phoneNumber, string phoneCodeHash, string code, string firstName, string lastName)
        {
            var request = new AuthSignUpRequest(phoneNumber, phoneCodeHash, code, firstName, lastName);
            await _protoSender.Send(request);

            OnUserAuthenticated(request.user, request.SessionExpires);

            return request.user;
        }

        private void OnUserAuthenticated(User user, int sessionExpiration)
        {
            _session.User = user;
            _session.SessionExpires = sessionExpiration;

            _session.Save();
        }

        public async Task<InputFile> UploadFile(string name, byte[] data)
        {
            var partSize = 65536;

            var fileId = DateTime.Now.Ticks;

            var partedData = new Dictionary<int, byte[]>();
            var parts = Convert.ToInt32(Math.Ceiling(Convert.ToDouble(data.Length) / Convert.ToDouble(partSize)));
            var remainBytes = data.Length;
            for (int i = 0; i < parts; i++)
            {
                partedData.Add(i, data
                    .Skip(i * partSize)
                    .Take(remainBytes < partSize ? remainBytes : partSize)
                    .ToArray());

                remainBytes -= partSize;
            }

            for (int i = 0; i < parts; i++)
            {
                var saveFilePartRequest = new Upload_SaveFilePartRequest(fileId, i, partedData[i]);
                await _protoSender.Send(saveFilePartRequest);

                if (saveFilePartRequest.Done == false)
                    throw new InvalidOperationException($"File part {i} does not uploaded");
            }

            string md5Checksum;
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(data);
                var hashResult = new StringBuilder(hash.Length * 2);

                for (int i = 0; i < hash.Length; i++)
                    hashResult.Append(i.ToString("x2"));

                md5Checksum = hashResult.ToString();
            }

            var inputFile = new InputFileConstructor(fileId, parts, name, md5Checksum);

            return inputFile;
        }

        public async Task<bool> SendMediaMessage(int contactId, InputFile file)
        {
            var request = new Message_SendMediaRequest(
                new InputPeerContactConstructor(contactId),
                new InputMediaUploadedPhotoConstructor(file));

            await _protoSender.Send(request);

            return true;
        }

        public async Task<int?> ImportContactByPhoneNumber(string phoneNumber)
        {
            if (!validateNumber(phoneNumber))
                throw new InvalidOperationException("Invalid phone number. It should be only digit string, from 5 to 20 digits.");

            var request = new ImportContactRequest(new InputPhoneContactConstructor(0, phoneNumber, "My Test Name", String.Empty));
            await _protoSender.Send(request);

            var importedUser = (ImportedContactConstructor)request.imported.FirstOrDefault();

            return importedUser?.user_id;
        }

        public async Task<int?> ImportByUserName(string username)
        {
            var request = new ImportByUserName(username);
            await _protoSender.Send(request);

            return request.id;
        }

        public async Task SendMessage(int id, string message)
        {
            var request = new SendMessageRequest(new InputPeerContactConstructor(id), message);
            await _protoSender.Send(request);
        }

        public async Task<List<Message>> GetMessagesHistoryForContact(int userId, int offset, int limit, int maxId = -1)
        {
            var request = new GetHistoryRequest(new InputPeerContactConstructor(userId), offset, maxId, limit);
            await _protoSender.Send(request);

            return request.messages;
        }

        public async Task<Tuple<storage_FileType, byte[]>> GetFile(long volumeId, int localId, long secret, int offset, int limit)
        {
            var request = new GetFileRequest(new InputFileLocationConstructor(volumeId, localId, secret), offset, limit);
            await _protoSender.Send(request);

            return Tuple.Create(request.type, request.bytes);
        }

        public async Task<MessageDialogs> GetDialogs(int offset, int limit, int maxId = 0)
        {
            var request = new GetDialogsRequest(offset, maxId, limit);
            await _protoSender.Send(request);

            return new MessageDialogs
            {
                Dialogs = request.dialogs,
                Messages = request.messages,
                Chats = request.chats,
                Users = request.users
            };
        }

        public async Task<UserFull> GetUserFull(int userId)
        {
            var request = new GetUserFullRequest(userId);
            await _protoSender.Send(request);

            return request._userFull;
        }

        private bool validateNumber(string number)
        {
            var regex = new Regex("^\\d{7,20}$");

            return regex.IsMatch(number);
        }

        public async Task<ContactsContacts> GetContacts(IList<int> contactIds = null)
        {
            var request = new GetContactsRequest(contactIds);
            await _protoSender.Send(request);

            return new ContactsContacts
            {
                Contacts = request.Contacts,
                Users = request.Users
            };
        }

        protected virtual void OnUpdateMessage(object sender, Updates e)
        {
            UpdateMessage?.Invoke(this, e);
        }

        public Task Close()
        {
            return CloseCurrentTransport();
        }
    }
}
