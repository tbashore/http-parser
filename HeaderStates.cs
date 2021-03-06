﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HttpParser
{
    public enum HeaderStates
    {
        h_general = 0,
        h_C,
        h_CO,
        h_CON,

        h_matching_connection,
        h_matching_proxy_connection,
        h_matching_content_length,
        h_matching_transfer_encoding,
        h_matching_upgrade,

        h_connection,
        h_content_length,
        h_transfer_encoding,
        h_upgrade,

        h_matching_transfer_encoding_chunked,
        h_matching_connection_keep_alive,
        h_matching_connection_close,

        h_transfer_encoding_chunked,
        h_connection_keep_alive,
        h_connection_close
    }
}
