##! Heartbleed vulnerability detection script for Zeek
##! Detects potential CVE-2014-0160 exploitation attempts

module Heartbleed;

export {
    redef enum Notice::Type += {
        ## Indicates a potential Heartbleed exploitation attempt
        Heartbleed_Attempt,
        ## Indicates a likely successful Heartbleed exploitation
        Heartbleed_Success,
        ## Indicates an abnormal heartbeat payload size
        Heartbleed_Anomaly
    };

    ## Configure whether to log detailed heartbeat messages
    const log_heartbeat_details = T &redef;

    ## Threshold for suspicious payload size differences
    const suspicious_size_difference = 16384 &redef;

    ## Track connections with heartbeat activities
    global heartbeat_connections: table[conn_id] of count &create_expire = 1 hrs;
}

# TLS Record Types
const TLS_HEARTBEAT = 24;

# TLS Heartbeat Message Types
const HEARTBEAT_REQUEST  = 1;
const HEARTBEAT_RESPONSE = 2;

type HeartbeatInfo: record {
    message_type: count;
    payload_length: count;
    actual_length: count;
    timestamp: time;
};

# Track heartbeat details per connection
global conn_heartbeats: table[conn_id] of vector of HeartbeatInfo &create_expire = 1 hrs;

event ssl_heartbeat(c: connection, is_orig: bool, length: count, heartbeat_type: count, payload_length: count, payload: string)
{
    local cid = c$id;
    
    # Initialize connection tracking if needed
    if (cid !in heartbeat_connections)
        heartbeat_connections[cid] = 0;
    
    heartbeat_connections[cid] += 1;

    # Create heartbeat info record
    local hb_info = HeartbeatInfo($message_type=heartbeat_type,
                                $payload_length=payload_length,
                                $actual_length=|payload|,
                                $timestamp=network_time());

    # Track heartbeat details
    if (cid !in conn_heartbeats)
        conn_heartbeats[cid] = vector();
    conn_heartbeats[cid][|conn_heartbeats[cid]|] = hb_info;

    # Check for potential Heartbleed attempts
    if (heartbeat_type == HEARTBEAT_REQUEST) {
        if (payload_length > suspicious_size_difference) {
            NOTICE([
                $note=Heartbleed_Attempt,
                $conn=c,
                $msg=fmt("Potential Heartbleed attempt detected. Requested payload size: %d", payload_length),
                $sub=fmt("From: %s, To: %s", c$id$orig_h, c$id$resp_h),
                $identifier=cat(c$id$orig_h, c$id$resp_h)
            ]);
        }
    }

    # Check response for potential data leakage
    if (heartbeat_type == HEARTBEAT_RESPONSE) {
        if (|payload| > payload_length) {
            NOTICE([
                $note=Heartbleed_Success,
                $conn=c,
                $msg=fmt("Potential Heartbleed exploitation. Response larger than requested: %d > %d",
                        |payload|, payload_length),
                $sub=fmt("From: %s, To: %s", c$id$orig_h, c$id$resp_h),
                $identifier=cat(c$id$orig_h, c$id$resp_h)
            ]);
        }
    }

    # Check for anomalous size differences
    if (|payload| > 0 && payload_length > 0) {
        local size_diff = |payload| - payload_length;
        if (size_diff > suspicious_size_difference) {
            NOTICE([
                $note=Heartbleed_Anomaly,
                $conn=c,
                $msg=fmt("Anomalous heartbeat size difference detected: %d", size_diff),
                $sub=fmt("From: %s, To: %s", c$id$orig_h, c$id$resp_h),
                $identifier=cat(c$id$orig_h, c$id$resp_h)
            ]);
        }
    }
}

# Log summary of heartbeat activities when connection ends
event connection_state_remove(c: connection)
{
    local cid = c$id;
    if (cid in heartbeat_connections) {
        if (log_heartbeat_details && cid in conn_heartbeats) {
            for (idx in conn_heartbeats[cid]) {
                local hb = conn_heartbeats[cid][idx];
                Log::write(Heartbleed::LOG, [
                    $ts=hb$timestamp,
                    $uid=c$uid,
                    $id=cid,
                    $message_type=hb$message_type,
                    $payload_length=hb$payload_length,
                    $actual_length=hb$actual_length
                ]);
            }
        }
        delete heartbeat_connections[cid];
        if (cid in conn_heartbeats)
            delete conn_heartbeats[cid];
    }
}

# Define logging
export {
    # Create a new logging stream
    redef enum Log::ID += { LOG };

    type Info: record {
        ts: time &log;
        uid: string &log;
        id: conn_id &log;
        message_type: count &log;
        payload_length: count &log;
        actual_length: count &log;
    };
}

event zeek_init() &priority=5
{
    Log::create_stream(Heartbleed::LOG, [$columns=Info, $path="heartbleed"]);
}

# Add summarization for better context
function summarize_heartbeats(c: connection): string
{
    local cid = c$id;
    if (cid !in conn_heartbeats)
        return "No heartbeat activity";

    local summary = "";
    local requests = 0;
    local responses = 0;
    local max_size_diff = 0;

    for (idx in conn_heartbeats[cid]) {
        local hb = conn_heartbeats[cid][idx];
        if (hb$message_type == HEARTBEAT_REQUEST)
            ++requests;
        else if (hb$message_type == HEARTBEAT_RESPONSE)
            ++responses;

        local size_diff = hb$actual_length - hb$payload_length;
        if (size_diff > max_size_diff)
            max_size_diff = size_diff;
    }

    return fmt("Requests: %d, Responses: %d, Max size difference: %d",
              requests, responses, max_size_diff);
}
