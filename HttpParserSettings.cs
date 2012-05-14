using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace HttpParser
{
    public class HttpParserSettings
    {
        internal delegate int http_cb(HttpParser parser);
        internal unsafe delegate int http_data_cb(HttpParser parser, byte* at, uint length);

        internal http_cb on_message_begin { get; set; }
        internal http_data_cb on_url { get; set; }
        internal http_data_cb on_header_field { get; set; }
        internal http_data_cb on_header_value { get; set; }
        internal http_cb on_headers_complete { get; set; }
        internal http_data_cb on_body { get; set; }
        internal http_cb on_message_complete { get; set; }

        public delegate bool Notification(HttpParser parser);
        public delegate bool Data(HttpParser parser, byte[] data);

        public Notification MessageBegan { get; set; }
        public Data Url { get; set; }
        public Data HeaderField { get; set; }
        public Data HeaderValue { get; set; }
        public Notification HeadersCompleted { get; set; }
        public Data Body { get; set; }
        public Notification MessageCompleted { get; set; }

        internal unsafe void Wrap()
        {
            if (MessageBegan != null) { on_message_begin = on_message_begin_wrapper; }
            if (Url != null) { on_url = on_url_wrapper; }
            if (HeaderField != null) { on_header_field = on_header_field_wrapper; }
            if (HeaderValue != null) { on_header_value = on_header_value_wrapper; }
            if (HeadersCompleted != null) { on_headers_complete = on_headers_complete_wrapper; }
            if (Body != null) { on_body = on_body_wrapper; }
            if (MessageCompleted != null) { on_message_complete = on_message_complete_wrapper; }
        }

        internal int on_message_begin_wrapper(HttpParser parser)
        {
            return Convert.ToInt32(MessageBegan(parser));
        }

        internal unsafe int on_url_wrapper(HttpParser parser, byte* at, uint length)
        {
            var data = new byte[(int)length];
            Marshal.Copy((IntPtr)at, data, 0, (int)length);
            return Convert.ToInt32(Url(parser, data));
        }

        internal unsafe int on_header_field_wrapper(HttpParser parser, byte* at, uint length)
        {
            var data = new byte[(int)length];
            Marshal.Copy((IntPtr)at, data, 0, (int)length);
            return Convert.ToInt32(HeaderField(parser, data));
        }

        internal unsafe int on_header_value_wrapper(HttpParser parser, byte* at, uint length)
        {
            var data = new byte[(int)length];
            Marshal.Copy((IntPtr)at, data, 0, (int)length);
            return Convert.ToInt32(HeaderValue(parser, data));
        }

        internal int on_headers_complete_wrapper(HttpParser parser)
        {
            return Convert.ToInt32(HeadersCompleted(parser));
        }

        internal unsafe int on_body_wrapper(HttpParser parser, byte* at, uint length)
        {
            var data = new byte[(int)length];
            Marshal.Copy((IntPtr)at, data, 0, (int)length);
            return Convert.ToInt32(Body(parser, data));
        }

        internal int on_message_complete_wrapper(HttpParser parser)
        {
            return Convert.ToInt32(MessageCompleted(parser));
        }
    }
}
