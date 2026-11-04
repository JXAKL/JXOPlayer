-- Used only for formats without a fast container header reader.
-- Playback starts paused; report demuxer metadata and exit before decoding.
mp.register_event("file-loaded", function()
    io.write("JXO_DURATION=" .. tostring(mp.get_property_number("duration", 0)) .. "\n")
    io.flush()
    mp.commandv("quit")
end)
