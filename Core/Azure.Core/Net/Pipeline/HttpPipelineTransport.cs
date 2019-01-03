﻿using Azure.Core.Buffers;
using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static System.Buffers.Text.Encodings;

namespace Azure.Core.Net.Pipeline
{
    // TODO (pri 2): implement chunked encoding
    public class HttpPipelineTransport : PipelineTransport
    {
        static readonly HttpClient s_client = new HttpClient();

        public sealed override PipelineCallContext CreateContext(ref PipelineOptions options, CancellationToken cancellation, ServiceMethod method, Uri uri)
            => new Context(options.Pool, cancellation, method, uri);
            
        public sealed override async Task ProcessAsync(PipelineCallContext context)
        {
            var httpTransportContext = context as Context;
            if (httpTransportContext == null) throw new InvalidOperationException("the context is not compatible with the transport");

            var httpRequest = httpTransportContext.BuildRequestMessage();

            HttpResponseMessage responseMessage = await ProcessCoreAsync(context.Cancellation, httpRequest).ConfigureAwait(false);
            httpTransportContext.ProcessResponseMessage(responseMessage);
        }

        protected virtual async Task<HttpResponseMessage> ProcessCoreAsync(CancellationToken cancellation, HttpRequestMessage httpRequest)
            => await s_client.SendAsync(httpRequest, cancellation).ConfigureAwait(false);

        class Context : PipelineCallContext
        {
            HttpRequestMessage _requestMessage;

            Sequence<byte> _content = new Sequence<byte>(); // TODO (pri 1): content should be either stream or Sequence
            string _contentTypeHeaderValue;
            string _contentLengthHeaderValue;

            HttpResponseMessage _responseMessage;
            
            public Context(ArrayPool<byte> pool, CancellationToken cancellation, ServiceMethod method, Uri uri) 
                : base(uri, cancellation)
            {
                _content = new Sequence<byte>(pool);
                _requestMessage = new HttpRequestMessage();
                _requestMessage.Method = ToHttpClientMethod(method);
                _requestMessage.RequestUri = uri;

                string hostString = uri.Host;
                AddHeader("Host", hostString);
            }

            #region Request
            public override void AddHeader(Header header)
            {
                var valueString = Utf8.ToString(header.Value);
                var nameString = Utf8.ToString(header.Name);
                AddHeader(nameString, valueString);
            }

            public override void AddHeader(string name, string value)
            {
                if (name.Equals("Content-Type", StringComparison.InvariantCulture)) {
                    _contentTypeHeaderValue = value;
                }
                else if (name.Equals("Content-Length", StringComparison.InvariantCulture))
                {
                    _contentLengthHeaderValue = value;
                }
                else {
                    if(!_requestMessage.Headers.TryAddWithoutValidation(name, value))
                    {
                        throw new NotImplementedException();
                    }
                }
            }

            protected internal override Memory<byte> GetRequestBuffer(int minimumSize) => _content.GetMemory(minimumSize);

            protected internal override void CommitRequestBuffer(int size) => _content.Advance(size);

            protected internal override Task FlushAsync()
                => Task.CompletedTask;

            internal HttpRequestMessage BuildRequestMessage()
            {
                // A copy of a message needs to be made because HttpClient does not allow sending the same message twice,
                // and so the retry logic fails.
                var message = new HttpRequestMessage(_requestMessage.Method, _requestMessage.RequestUri);
                foreach (var header in _requestMessage.Headers) {
                    if(!message.Headers.TryAddWithoutValidation(header.Key, header.Value)) {
                        throw new Exception("could not add header " + header.ToString());
                    }
                }

                if (_content.Length != 0) {
                    if (RequestContentSource != null) throw new InvalidOperationException("cannot both write to content and specify content source");
                    message.Content = new ReadOnlySequenceContent(_content.AsReadOnly(), Cancellation);
                    message.Content.Headers.Add("Content-Type", _contentTypeHeaderValue);
                    message.Content.Headers.Add("Content-Length", _contentLengthHeaderValue);
                }
                else if(RequestContentSource != null) {
                    message.Content = new StreamContent(RequestContentSource); 
                    message.Content.Headers.Add("Content-Type", _contentTypeHeaderValue);
                    message.Content.Headers.Add("Content-Length", _contentLengthHeaderValue);
                }

                return message;
            }
            #endregion

            #region Response
            internal void ProcessResponseMessage(HttpResponseMessage response) { _responseMessage = response; }

            protected internal override int Status => (int)_responseMessage.StatusCode;

            protected internal override bool TryGetHeader(ReadOnlySpan<byte> name, out ReadOnlySpan<byte> value)
            {
                string nameString = Utf8.ToString(name);
                if (!_responseMessage.Headers.TryGetValues(nameString, out var values)) {
                    if (!_responseMessage.Content.Headers.TryGetValues(nameString, out values)) {
                        value = default;
                        return false;
                    }
                }

                var e = values.GetEnumerator();
                if(!e.MoveNext()) {
                    value = default;
                    return false;
                }

                string first = e.Current;
                if(!e.MoveNext()) {
                    value = Encoding.UTF8.GetBytes(first);
                    return true;
                }

                var all = string.Join(",", values);
                value = Encoding.ASCII.GetBytes(all);
                return true;
            }

            protected internal override ReadOnlySequence<byte> ResponseContent => _content.AsReadOnly();

            protected internal override ReadOnlySequence<byte> RequestContent => _content.AsReadOnly();

            protected internal override Stream ResponseStream => _responseMessage.Content.ReadAsStreamAsync().Result;

            protected internal async override Task<ReadOnlySequence<byte>> ReadContentAsync(long minimumLength)
            { 
                _content.Dispose();
                var contentStream = await _responseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false);
                while (true)
                {
                    var length = _content.Length;
                    if (length >= minimumLength) return _content.AsReadOnly(); // TODO (pri 3): is this ok when minimumLength is zero?
                    _content = await contentStream.ReadAsync(_content, Cancellation).ConfigureAwait(false);
                }
            }

            protected internal override void DisposeResponseContent(long bytes)
            {
                // TODO (pri 2): this should dispose already read segments
            }
            #endregion

            public override void Dispose()
            {
                _content.Dispose();
                base.Dispose();
            }

            public override string ToString() => _requestMessage.ToString();

            public static HttpMethod ToHttpClientMethod(ServiceMethod method)
            {
                switch (method)
                {
                    case ServiceMethod.Get: return HttpMethod.Get;
                    case ServiceMethod.Post: return HttpMethod.Post;
                    case ServiceMethod.Put: return HttpMethod.Put;
                    case ServiceMethod.Delete: return HttpMethod.Delete;

                    default: throw new NotImplementedException();
                }
            }
        }
    }
}