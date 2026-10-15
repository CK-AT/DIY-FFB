#include <CANManager.h>
#include <CommManager.h>
#include <ConfigManager.h>
#include <Esp.h>
#include <Joystick_ESP32S2.h>
#include <LogOutput.h>
#include <SerialManager.h>
#include <Version.h>
#include <Version_Board.h>

#include "queue.h"

// RTDebugOutputService debugOutput = RTDebugOutputService();
QueueHandle_t _log_queue_data;

#ifndef GIT_HASH
    #define GIT_HASH ""
#endif

namespace {
constexpr uint32_t k_ota_wifi_timeout_us = 20000000;
constexpr uint32_t k_axis_ota_quiet_window_us = 180000000;
constexpr uint8_t k_force_pos_decimation = 10;

void copy_string(char *dest, size_t dest_len, const char *src) {
    if (!dest || dest_len == 0) return;
    if (!src) {
        dest[0] = '\0';
        return;
    }
    strncpy(dest, src, dest_len - 1);
    dest[dest_len - 1] = '\0';
}

bool time_reached(uint32_t now, uint32_t deadline) {
    return static_cast<int32_t>(now - deadline) >= 0;
}

bool has_multiple_linked_axes(const AxisID *linked_axes) {
    if (!linked_axes) return false;
    uint8_t count = 0;
    for (uint8_t idx = 0; idx < (sizeof(FunctionBase::linked_axes) / sizeof(FunctionBase::linked_axes[0])); idx++) {
        AxisID axis_id = AxisID(linked_axes[idx] & AxisID_AXIS_ID_MASK);
        if (!MessageTools::check_axis_id(axis_id)) return false;
        count++;
        if (count > 1) return true;
    }
    return false;
}
}  // namespace

Joystick_ _joystick =
    Joystick_(JOYSTICK_DEFAULT_REPORT_ID, JOYSTICK_TYPE_GAMEPAD, CommManager::JOYSTICK_BUTTON_COUNT, 0,  // Button Count, Hat Switch Count
              true, true, true,                                                                          // X, Y, Z
              true, true, true,                                                                          // Rx, Ry, Rz
              true, true,                                                                                // rudder, throttle
              true, true, true);                                                                         // accelerator, brake, steering

void CommManager::periodic_task_func(void) {
    if (!_config_manager_initialized && (_config_manager->get_mode() != ConfigManager::MODE_UNDEFINED)) {
        setup_can(_can_config);
        setup_joystick();
        _config_manager_initialized = true;
        refresh_force_pos_rate(_config_manager->get_function_config()->base.linked_axes);
    } else {
        if (!_physics_task_started || _ota_state != OtaState::OTA_IDLE) {
            // process() is usually called by the physics task, but it hasn't been
            // started yet (gateway) or is suspended during OTA. Pump it here so
            // CAN/ISOTP keeps flowing — notably axis log egress to the gateway,
            // which otherwise goes silent while updating. (During the blocking
            // download itself periodic_task can't loop, so logs still pause then.)
            process();
        }
        can_manager.process_isotp();
        uint32_t now = micros();
        if ((now - ti_last_state_updates) > 100000) {
            ti_last_state_updates = now;
            uint8_t online_flags = 0;
            if (is_gateway()) {
                for (uint8_t axis_idx = 0; axis_idx < MessageTools::MAX_AXES_COUNT; axis_idx++) {
                    AxisID axis_id = MessageTools::axis_id_from_index(axis_idx);
                    send_axis_state_message(axis_id, online_flags);
                }
            } else {
                send_axis_state_message(get_axis_id(), online_flags);
            }
            send_gateway_state_message(_config_manager->get_gateway_id(), online_flags);
        }
        if ((now - ti_last_joystick_update) > 10000) {
            ti_last_joystick_update = now;
            send_joystick_values();
        }
        if (is_gateway()) {
            // Master DDS phase advance every periodic tick (~1 ms);
            // broadcast 0x0F0 sync frame at 100 Hz.
            _master_dds.tick(now);
            if ((now - _ti_last_dds_sync) > 10000 && active_downlink_channel != nullptr) {
                _ti_last_dds_sync = now;
                active_downlink_channel->send_dds_sync(
                    _master_dds.get_fundamental(0), _master_dds.get_phase(0),
                    _master_dds.get_fundamental(1), _master_dds.get_phase(1));
            }
        }
        if (!_device_info_sent) {
            _device_info_sent = send_device_info(is_gateway() ? CommChannel::USB_SERIAL : CommChannel::ISOTP);
        }
    }
    update_joystick_state();
    update_ota_state();
    pump_log(5);
}

void CommManager::setup_joystick() {
    // Unique PID per device so the OS distinguishes them reliably.
    // Gateway N: 0x8210 + N  (gateway 1 = 0x8211 for backward compat)
    // Axis N:    0x8220 + N
    USB.VID(0x303b);
    if (_is_gateway) {
        USB.PID(0x8210 + get_gateway_id());
        snprintf(_usb_product_name, sizeof(_usb_product_name) - 1, "DIY-FFB-Gateway-%d", get_gateway_id());
    } else {
        USB.PID(0x8220 + get_axis_id());
        snprintf(_usb_product_name, sizeof(_usb_product_name) - 1, "DIY-FFB-Axis-%d", get_axis_id());
    }
    USB.productName(_usb_product_name);
    USB.manufacturerName("OpenSource");
    USB.begin();
    switch_joystick_state(JOYSTICK_USB_UP);
}

void CommManager::update_joystick_state() {
    switch (_joystick_state) {
        case JOYSTICK_USB_UP:
            if ((micros() - _ti_joystick_state) > 1000000) {
                _joystick.setXAxisRange(JOYSTICK_MIN, JOYSTICK_MAX);
                _joystick.setYAxisRange(JOYSTICK_MIN, JOYSTICK_MAX);
                _joystick.setZAxisRange(JOYSTICK_MIN, JOYSTICK_MAX);
                _joystick.setRxAxisRange(JOYSTICK_MIN, JOYSTICK_MAX);
                _joystick.setRyAxisRange(JOYSTICK_MIN, JOYSTICK_MAX);
                _joystick.setRzAxisRange(JOYSTICK_MIN, JOYSTICK_MAX);
                _joystick.setRudderRange(JOYSTICK_MIN, JOYSTICK_MAX);
                _joystick.setThrottleRange(JOYSTICK_MIN, JOYSTICK_MAX);
                _joystick.setAcceleratorRange(JOYSTICK_MIN, JOYSTICK_MAX);
                _joystick.setBrakeRange(JOYSTICK_MIN, JOYSTICK_MAX);
                _joystick.setSteeringRange(JOYSTICK_MIN, JOYSTICK_MAX);
                _joystick.begin(false);
                switch_joystick_state(JOYSTICK_PRE_READY);
            }
            break;
        case JOYSTICK_PRE_READY:
            if ((micros() - _ti_joystick_state) > 100000) {
                switch_joystick_state(JOYSTICK_READY);
            }
        default:
            break;
    }
}

void CommManager::update_ota_state() {
    ESP32OTAPull::ErrorCode result;
    switch (_ota_state) {
        case OTA_PREPARE_WIFI:
            if ((micros() - _ti_ota_state) > 100000) {
                // TODO: Deinit ESPNow
                LogOutput::printf("OTA: Initializing WiFi...");
                WiFi.begin(_wifi_info.ssid, _wifi_info.password);
                switch_ota_state(OTA_WAIT_FOR_WIFI);
            }
            break;
        case OTA_WAIT_FOR_WIFI:
            if (WiFi.isConnected()) {
                LogOutput::printf("OTA: WiFi online");
                switch_ota_state(OTA_CHECK);
            } else if ((micros() - _ti_ota_state) > k_ota_wifi_timeout_us) {
                LogOutput::printf("OTA: Failed to connect to WiFi within %u s", static_cast<unsigned>(k_ota_wifi_timeout_us / 1000000));
                switch_ota_state(OTA_ERROR);
            }
            break;
        case OTA_CHECK:
            if ((micros() - _ti_ota_state) > 100000) {
                ota.OverrideBoard(CONTROL_BOARD);
                result = ESP32OTAPull::ErrorCode(ota.CheckForOTAUpdate(_ota_url.c_str(), VERSION, ESP32OTAPull::ActionType::DONT_DO_UPDATE));
                switch (result) {
                    case ESP32OTAPull::ErrorCode::HTTP_FAILED:
                        LogOutput::printf("OTA: HTTP failed");
                        switch_ota_state(OTA_ERROR);
                        break;
                    case ESP32OTAPull::ErrorCode::JSON_PROBLEM:
                        LogOutput::printf("OTA: JSON problem");
                        switch_ota_state(OTA_ERROR);
                        break;
                    case ESP32OTAPull::ErrorCode::NO_UPDATE_AVAILABLE:
                        LogOutput::printf("OTA: No update available");
                        switch_ota_state(OTA_ERROR);
                        break;
                    case ESP32OTAPull::ErrorCode::NO_UPDATE_PROFILE_FOUND:
                        LogOutput::printf("OTA: No update profile found");
                        switch_ota_state(OTA_ERROR);
                        break;
                    case ESP32OTAPull::ErrorCode::UPDATE_AVAILABLE:
                        LogOutput::printf("OTA: Update available, installing...");
                        switch_ota_state(OTA_UPDATE);
                        break;
                    default:
                        LogOutput::printf("OTA: Negative HTTP response: %d", int(result));
                        switch_ota_state(OTA_ERROR);
                        break;
                }
            }
            break;
        case OTA_UPDATE:
            if ((micros() - _ti_ota_state) > 100000) {
                result = ESP32OTAPull::ErrorCode(ota.CheckForOTAUpdate(_ota_url.c_str(), VERSION));
                switch (result) {
                    case ESP32OTAPull::ErrorCode::OTA_UPDATE_FAIL:
                        LogOutput::printf("OTA: Failed to begin update: %s", ota.GetUpdateFailReason());
                        switch_ota_state(OTA_ERROR);
                        break;
                    case ESP32OTAPull::ErrorCode::WRITE_ERROR:
                        LogOutput::printf("OTA: Write error: %s at %d/%d bytes", ota.GetWriteFailReason(), ota.GetWriteFailOffset(),
                                          ota.GetWriteFailTotal());
                        switch_ota_state(OTA_ERROR);
                        break;
                    case ESP32OTAPull::ErrorCode::MD5_ERROR:
                        LogOutput::printf("OTA: MD5 error");
                        switch_ota_state(OTA_ERROR);
                        break;
                    default:
                        LogOutput::printf("OTA: Negative HTTP response: %d", int(result));
                        switch_ota_state(OTA_ERROR);
                        break;
                }
            }
            break;
        case OTA_ERROR:
            WiFi.disconnect(true);
            switch_ota_state(OTA_IDLE);
            break;
        default:
            break;
    }
}

void CommManager::send_gateway_state_message(GatewayID gateway_id, uint8_t online_flags) {
    _state_message.which_payload = Message_gateway_state_tag;
    _state_message.payload.gateway_state.axes_present = online_flags;
    _state_message.payload.gateway_state.rssi = 255;  // TODO: make this depend on active_intercom_channel
    _state_message.payload.gateway_state.gateway_id = gateway_id;
    send_message_to_host(_state_message);
}

bool CommManager::send_axis_state_message(AxisID axis_id, uint8_t &online_flags) {
    if (!MessageTools::check_axis_id(axis_id)) return false;
    if (is_online(axis_id)) {
        online_flags |= (1 << MessageTools::axis_index_from_id(axis_id));
        _state_message.which_payload = Message_axis_state_tag;
        _state_message.payload.axis_state.axis_id = axis_id;
        get_force(axis_id, _state_message.payload.axis_state.force);
        get_position(axis_id, _state_message.payload.axis_state.position);
        send_message_to_host(_state_message);
    }
    return true;
}

bool CommManager::send_device_info(CommChannel comm_channel) {
    Message msg = Message_init_zero;
    build_device_info_message(msg);
    if (is_gateway()) {
        return send_message_to_host(msg);
    }
    if (comm_channel == CommChannel::USB_SERIAL) {
        return send_message_to_host(msg);
    } else {
        return send_message_to_gateway(msg, comm_channel);
    }
}

void CommManager::build_device_info_message(Message &msg) {
    msg.which_payload = Message_device_info_tag;
    if (is_gateway()) {
        msg.payload.device_info.which_source = DeviceInfo_gateway_id_tag;
        msg.payload.device_info.source.gateway_id = get_gateway_id();
    } else {
        msg.payload.device_info.which_source = DeviceInfo_axis_id_tag;
        msg.payload.device_info.source.axis_id = get_axis_id();
    }

    copy_string(msg.payload.device_info.fw_version, sizeof(msg.payload.device_info.fw_version), VERSION);
    copy_string(msg.payload.device_info.build_timestamp, sizeof(msg.payload.device_info.build_timestamp), BUILD_TIMESTAMP);
    copy_string(msg.payload.device_info.git_hash, sizeof(msg.payload.device_info.git_hash), GIT_HASH);
    copy_string(msg.payload.device_info.board, sizeof(msg.payload.device_info.board), CONTROL_BOARD);

    uint64_t mac = ESP.getEfuseMac();
    char uid[13] = {};
    snprintf(uid, sizeof(uid), "%04x%08x", uint16_t(mac >> 32), uint32_t(mac));
    copy_string(msg.payload.device_info.device_uid, sizeof(msg.payload.device_info.device_uid), uid);
}

void CommManager::setup(Stream *serial, CANConfig &can_config, ConfigManager *config_manager, OnFFBAction on_ffb_action,
                        OnAxisAction on_axis_action, OnDdsSync on_dds_sync, GripReader *grip_reader) {
    _config_manager = config_manager;
    _on_ffb_action = on_ffb_action;
    _on_axis_action = on_axis_action;
    _on_dds_sync = on_dds_sync;
    _can_config = can_config;
    _grip_reader = grip_reader;
    _log_queue_data = xQueueCreate(20, MAX_LOG_LINE_LENGTH);
    setup_serial(serial);
    xTaskCreatePinnedToCore(this->periodic_task, "CommManagerTask", 8000, this, 1, nullptr, 0);
}

void CommManager::pump_log(int max_samples, int timeout) {
    char buff[MAX_LOG_LINE_LENGTH];
    while (max_samples) {
        // Peek first: a local-only line must not be held back by an uplink that
        // cannot currently accept relayed logs, otherwise CAN diagnostics are
        // suppressed precisely while the bus is in trouble.
        if (pdTRUE != xQueuePeek(_log_queue_data, buff, /*xTicksToWait=*/timeout)) break;
        bool local_only = (buff[0] == LOG_LOCAL_ONLY_MARK);
        if (!local_only && has_gateway()) {
            if (!active_uplink_channel->ready_to_receive_log_message()) return;
        }
        if (pdTRUE != xQueueReceive(_log_queue_data, buff, /*xTicksToWait=*/0)) break;
        const char *line = local_only ? buff + 1 : buff;
        if (_is_gateway) {
            send_gateway_log_msg(line);
        } else {
            send_axis_log_msg(line, local_only);
        }
        max_samples--;
    }
}

void CommManager::send_axis_log_msg(const char *buff, bool local_only) {
    log_msg.payload.axis_log_message.axis_id = get_axis_id();
    log_msg.which_payload = Message_axis_log_message_tag;
    memset(log_msg.payload.axis_log_message.msg, 0, sizeof(log_msg.payload.axis_log_message.msg));
    strncpy(log_msg.payload.axis_log_message.msg, buff, sizeof(log_msg.payload.axis_log_message.msg) - 1);
    // local_only: skip the ISO-TP relay, go straight out this axis's own USB.
    if (!local_only && has_gateway()) {
        send_message_to_gateway(log_msg, CommChannel::ISOTP);
    }
    send_message_to_host(log_msg);
}

void CommManager::send_gateway_log_msg(const char *buff) {
    log_msg.payload.gateway_log_message.gateway_id = get_gateway_id();
    log_msg.which_payload = Message_gateway_log_message_tag;
    memset(log_msg.payload.gateway_log_message.msg, 0, sizeof(log_msg.payload.gateway_log_message.msg));
    strncpy(log_msg.payload.gateway_log_message.msg, buff, sizeof(log_msg.payload.gateway_log_message.msg) - 1);
    send_message_to_host(log_msg);
}

bool CommManager::setup_serial(Stream *serial) {
    return serial_manager.setup(serial, this,
                                [this](const uint8_t *buffer, size_t size) { on_gateway_packet_received(buffer, size, CommChannel::USB_SERIAL); });
};

void CommManager::on_gateway_packet_received(const uint8_t *buffer, size_t size, CommChannel comm_channel) {
    Message msg = Message_init_zero;
    uint16_t crc = *reinterpret_cast<const uint16_t *>(buffer + size - sizeof(uint16_t));
    if (MessageTools::check_and_decode_message(msg, buffer, size - sizeof(uint16_t), crc)) {
        on_gateway_message(msg, buffer, size, comm_channel);
    }
}

void CommManager::on_gateway_message(const Message &msg, const uint8_t *protobuf_msg, uint16_t len_protobuf_msg, CommChannel comm_channel) {
    ConfigManager::UpdateResult update_result;
    bool force;
    switch (msg.which_payload) {
        case Message_axis_config_tag:
            if (_config_manager->get_mode() == ConfigManager::MODE_GATEWAY_ONLY) {
                send_message_to_axis(msg.payload.axis_config.axis_id, msg, protobuf_msg, len_protobuf_msg);
            } else {
                force = (comm_channel == CommChannel::USB_SERIAL) && !is_gateway();
                update_result = _config_manager->update_axis_config(msg.payload.axis_config, protobuf_msg, len_protobuf_msg, force);
                if (update_result == ConfigManager::UpdateResult::UPDATE_OTHER_AXIS) {
                    send_message_to_axis(msg.payload.axis_config.axis_id, msg, protobuf_msg, len_protobuf_msg);
                }
            }
            break;
        case Message_function_config_tag:
            if (_config_manager->is_axis()) {
                if (_config_manager->update_function_config(msg.payload.function_config, protobuf_msg, len_protobuf_msg) ==
                    ConfigManager::UPDATE_OK) {
                    send_active_function_message(comm_channel);
                    refresh_force_pos_rate(msg.payload.function_config.base.linked_axes);
                }
            } else {
                // gateway only, no need to call _config_manager->update_function_config()
                _config_manager->update_lookup_tables(msg.payload.function_config);
            }
            if (is_gateway() && (comm_channel == CommChannel::USB_SERIAL)) {
                const AxisID *linked_axes = msg.payload.function_config.base.linked_axes;
                for (uint8_t idx = 0; idx < (sizeof(FunctionBase::linked_axes) / sizeof(FunctionBase::linked_axes[0])); idx++) {
                    auto linked_axis_id = AxisID(linked_axes[idx] & AxisID_AXIS_ID_MASK);
                    send_message_to_axis(linked_axis_id, msg, protobuf_msg, len_protobuf_msg);
                }
            }
            break;
        case Message_ffb_action_tag:
            if (_config_manager->is_axis()) {
                on_ffb_action(msg.payload.ffb_action);
            }
            if (is_gateway() && (comm_channel == CommChannel::USB_SERIAL)) {
                // only the primary axis will process FFB actions, no need to send it to other axes
                AxisID primary_axis_id = _config_manager->get_primary_axis_id(msg.payload.ffb_action.function_id);
                if (MessageTools::check_axis_id(primary_axis_id)) {
                    send_message_to_axis(primary_axis_id, msg, protobuf_msg, len_protobuf_msg);
                }
            }
            break;
        case Message_dds_fundamentals_tag:
            if (is_gateway() && (comm_channel == CommChannel::USB_SERIAL)) {
                _master_dds.set_fundamental(0, msg.payload.dds_fundamentals.dds1_fundamental_hz);
                _master_dds.set_fundamental(1, msg.payload.dds_fundamentals.dds2_fundamental_hz);
            }
            break;
        case Message_axis_action_tag: {
            AxisID target_axis = msg.payload.axis_action.axis_id;
            if (_config_manager->is_axis() && _on_axis_action && (target_axis == _config_manager->get_axis_id())) {
                switch (msg.payload.axis_action.which_action) {
                    case AxisAction_return_axis_config_tag:
                        send_axis_config(comm_channel);
                        break;
                    case AxisAction_return_function_config_tag:
                        send_function_config(comm_channel);
                        break;
                    case AxisAction_return_active_function_tag:
                        send_active_function_message(comm_channel);
                        break;
                    default:
                        if (_on_axis_action) {
                            _on_axis_action(msg.payload.axis_action, comm_channel);
                        }
                        break;
                }
            }
            if (is_gateway() && (comm_channel == CommChannel::USB_SERIAL)) {
                if (!send_message_to_axis(target_axis, msg, protobuf_msg, len_protobuf_msg)) {
                    LogOutput::printf("Can't forward AxisAction: axis %d is offline", target_axis);
                }
            }
            break;
        }
        case Message_start_ota_update_tag: {
            OtaTarget target = msg.payload.start_ota_update.target;
            bool target_all = (target == OtaTarget_OTA_TARGET_UNSPECIFIED || target == OtaTarget_OTA_TARGET_ALL);
            bool target_axes = target_all || (target == OtaTarget_OTA_TARGET_AXES_ONLY);
            bool target_gateway = target_all || (target == OtaTarget_OTA_TARGET_GATEWAY_ONLY);
            AxisID target_axis_id = msg.payload.start_ota_update.target_axis_id;
            bool has_target_axis = MessageTools::check_axis_id(target_axis_id);
            if (!is_gateway() && target_axes && has_target_axis && (target_axis_id != get_axis_id())) {
                LogOutput::printf("OTA: Ignoring request for axis %d", int(target_axis_id));
                break;
            }
            if (is_gateway() && (comm_channel == CommChannel::USB_SERIAL) && target_axes) {
                if (has_target_axis) {
                    if (!send_message_to_axis(target_axis_id, msg)) {
                        LogOutput::printf("CommManager: Axis %d offline, OTA skipped", int(target_axis_id));
                    }
                } else {
                    for (int axis_idx = 0; axis_idx < MessageTools::MAX_AXES_COUNT; axis_idx++) {
                        send_message_to_axis(MessageTools::axis_id_from_index(axis_idx), msg);
                    }
                }
                _axis_ota_quiet_until = micros() + k_axis_ota_quiet_window_us;
            }
            if ((is_gateway() && target_gateway) || (!is_gateway() && target_axes)) {
                ota.AllowDowngrades(msg.payload.start_ota_update.allow_downgrades);
                _wifi_info = msg.payload.start_ota_update.wifi_info;
                _ota_url = msg.payload.start_ota_update.info_json_url;
                switch_ota_state(OtaState::OTA_PREPARE_WIFI);
            }
            break;
        }
        case Message_device_info_request_tag: {
            if (msg.payload.device_info_request.which_target == DeviceInfoRequest_gateway_id_tag) {
                if (is_gateway() && (msg.payload.device_info_request.target.gateway_id == get_gateway_id())) {
                    send_device_info(comm_channel);
                }
                break;
            }
            if (msg.payload.device_info_request.which_target == DeviceInfoRequest_axis_id_tag) {
                AxisID target_axis = msg.payload.device_info_request.target.axis_id;
                if (_config_manager->is_axis() && (target_axis == get_axis_id())) {
                    send_device_info(comm_channel);
                    break;
                }
                if (is_gateway() && (comm_channel == CommChannel::USB_SERIAL)) {
                    if (!send_message_to_axis(target_axis, msg, protobuf_msg, len_protobuf_msg)) {
                        LogOutput::printf("Can't forward DeviceInfoRequest: axis %d is offline", target_axis);
                    }
                }
            }
            break;
        }
        default:
            LogOutput::printf("Unknown Message received");
            break;
    }
}

void CommManager::send_active_function_message(AxisID axis_id, FunctionID function_id, CommChannel comm_channel) {
    Message msg;
    msg.which_payload = Message_active_function_tag;
    msg.payload.active_function.axis_id = axis_id;
    msg.payload.active_function.function_id = function_id;
    send_message_to_gateway(msg, comm_channel);
}

void CommManager::send_active_function_message(CommChannel comm_channel) {
    send_active_function_message(_config_manager->get_axis_id(), _config_manager->get_function_id(), comm_channel);
}

void CommManager::send_axis_config(CommChannel comm_channel) {
    const uint8_t *raw_data = nullptr;
    uint16_t raw_len = 0;
    if (_config_manager && _config_manager->get_axis_config_raw(raw_data, raw_len)) {
        Message msg = Message_init_zero;
        msg.which_payload = Message_axis_config_tag;
        if (comm_channel == CommChannel::USB_SERIAL) {
            serial_manager.send_message_to_host(msg, raw_data, raw_len);
            return;
        }
        if (has_gateway()) {
            active_uplink_channel->send_message_to_gateway(msg, raw_data, raw_len);
            return;
        }
    }
    Message msg;
    _config_manager->get_axis_config_as_message(msg);
    send_message_to_gateway(msg, comm_channel);
}

void CommManager::send_function_config(CommChannel comm_channel) {
    Message msg;
    _config_manager->get_function_config_as_message(msg);
    send_message_to_gateway(msg, comm_channel);
}

void CommManager::on_axis_packet_received(AxisID axis_id, const uint8_t *data, size_t len, CommChannel comm_channel) {
    Message msg = Message_init_zero;
    uint16_t crc = *reinterpret_cast<const uint16_t *>(data + len - sizeof(uint16_t));
    if (MessageTools::check_and_decode_message(msg, data, len - sizeof(uint16_t), crc)) {
        on_axis_message(axis_id, msg, data, len, comm_channel);
    }
}

void CommManager::on_axis_message(AxisID axis_id, const Message &msg, const uint8_t *protobuf_msg, uint16_t len_protobuf_msg,
                                  CommChannel comm_channel) {
    switch (msg.which_payload) {
        case Message_function_config_tag:
            _config_manager->update_lookup_tables(msg.payload.function_config);
            send_active_function_message(axis_id, msg.payload.function_config.base.function_id, CommChannel::USB_SERIAL);
            break;
    }
    serial_manager.send_message_to_host(msg, protobuf_msg, len_protobuf_msg);
}

bool CommManager::setup_can(CANConfig &config) {
    AxisID axis_id = get_axis_id();
    _is_axis = MessageTools::check_axis_id(axis_id);
    _is_gateway = !_is_axis;
    can_manager.setup(axis_id, config.baud_rate, config.tx_pin, config.rx_pin,
                      std::bind(&CommManager::on_gateway_packet_received, this, std::placeholders::_1, std::placeholders::_2, CommChannel::ISOTP),
                      std::bind(&CommManager::on_ffb_action, this, std::placeholders::_1),
                      std::bind(&CommManager::on_axis_packet_received, this, std::placeholders::_1, std::placeholders::_2, std::placeholders::_3,
                                CommChannel::ISOTP),
                      std::bind(&CommManager::on_axis_state_change, this, std::placeholders::_1, std::placeholders::_2),
                      std::bind(&CommManager::on_gateway_state_change, this, std::placeholders::_1, std::placeholders::_2),
                      _on_dds_sync);
    active_intercom_channel = &can_manager;
    if (_is_gateway) {
        active_downlink_channel = &can_manager;
        can_manager.set_gateway_mode(true);
    }
    return true;
}

void CommManager::on_axis_state_change(AxisID axis_id, bool is_online) {
    if (axis_id == get_axis_id()) return;
    if (!is_gateway()) return;
    uint32_t now = micros();
    if (_axis_ota_quiet_until && !time_reached(now, _axis_ota_quiet_until)) return;
    if (_ota_state != OTA_IDLE) return;
    if (is_online) {
        LogOutput::printf("CommManager: Axis %d online, requesting function config", axis_id);
        if (!is_gateway()) return;
        Message msg = Message_init_default;
        msg.which_payload = Message_axis_action_tag;
        msg.payload.axis_action.axis_id = axis_id;
        msg.payload.axis_action.which_action = AxisAction_return_function_config_tag;
        msg.payload.axis_action.action.return_function_config = true;
        send_message_to_axis(axis_id, msg);
    } else {
        LogOutput::printf("CommManager: Axis %d offline", axis_id);
    }
}

void CommManager::refresh_force_pos_rate(const AxisID *linked_axes) {
    _force_pos_full_rate = has_multiple_linked_axes(linked_axes);
    _force_pos_tick = 0;
}

void CommManager::on_gateway_state_change(ICommChannel *comm_channel, bool is_online) {
    if (is_online) {
        if (!active_uplink_channel) {
            LogOutput::printf("CommManager: Uplink online");
            active_uplink_channel = comm_channel;
        }
    } else if (active_uplink_channel == comm_channel) {
        LogOutput::printf("CommManager: Uplink lost");
        active_uplink_channel = nullptr;
    }
}

void CommManager::process(void) {
    can_manager.process();
}

bool CommManager::send_force_and_position(float &f_contact_point, float &x_contact_point) {
    _f_contact_point_own = f_contact_point;
    _x_contact_point_own = x_contact_point;
    if (_force_pos_full_rate) {
        _force_pos_tick = 0;
        return can_manager.send_force_and_position(f_contact_point, x_contact_point);
    }
    _force_pos_tick++;
    if (_force_pos_tick < k_force_pos_decimation) return true;
    _force_pos_tick = 0;
    return can_manager.send_force_and_position(f_contact_point, x_contact_point);
}

bool CommManager::update_position_limits(float x_contact_point_min, float x_contact_point_max) {
    can_manager.update_position_limits(x_contact_point_min, x_contact_point_max);
    return true;
}

bool CommManager::update_function_id(FunctionID function_id) {
    can_manager.update_function_id(function_id);
    return true;
}

bool CommManager::get_force(AxisID axis_id, float &f_contact_point) {
    if (axis_id == get_axis_id()) {
        f_contact_point = _f_contact_point_own;
        return true;
    }
    if (!active_intercom_channel) return false;
    return active_intercom_channel->get_force(axis_id, f_contact_point);
}

bool CommManager::update_force(float &f_contact_point) {
    _f_contact_point_own = f_contact_point;
    return true;
}

bool CommManager::get_position(AxisID axis_id, float &x_contact_point) {
    if (axis_id == get_axis_id()) {
        x_contact_point = _x_contact_point_own;
        return true;
    }
    if (!active_intercom_channel) return false;
    return active_intercom_channel->get_position(axis_id, x_contact_point);
}

bool CommManager::get_position_limits(AxisID axis_id, float &x_contact_point_min, float &x_contact_point_max) {
    if (!active_intercom_channel) return false;
    return active_intercom_channel->get_position_limits(axis_id, x_contact_point_min, x_contact_point_max);
}

bool CommManager::get_function_id(AxisID axis_id, FunctionID &function_id) {
    if (axis_id == get_axis_id()) {
        function_id = _config_manager->get_function_id();
        return true;
    }
    if (!active_intercom_channel) return false;
    return active_intercom_channel->get_function_id(axis_id, function_id);
}

bool CommManager::is_online(AxisID axis_id) {
    if (axis_id == get_axis_id()) return true;
    if (!active_intercom_channel) return false;
    return active_intercom_channel->is_online(axis_id);
}

bool CommManager::send_message_to_axis(AxisID axis_id, const Message &message) {
    if (!is_gateway()) return false;
    if (!active_downlink_channel->is_online(axis_id)) return false;
    uint8_t tx_buffer[MessageTools::MAX_ENCODED_SIZE + 2];
    uint16_t crc;
    uint16_t num_bytes_encoded = MessageTools::encode_message_and_calc_crc(message, tx_buffer, MessageTools::MAX_ENCODED_SIZE, crc);
    if (num_bytes_encoded) {
        memcpy(tx_buffer + num_bytes_encoded, &crc, sizeof(uint16_t));
        return active_downlink_channel->send_message_to_axis(axis_id, message, tx_buffer, num_bytes_encoded + sizeof(uint16_t));
    }
    return false;
}

bool CommManager::send_message_to_axis(AxisID axis_id, const Message &message, const uint8_t *raw_data, uint32_t len_raw_data) {
    if (!is_gateway()) return false;
    if (!active_downlink_channel->is_online(axis_id)) return false;
    return active_downlink_channel->send_message_to_axis(axis_id, message, raw_data, len_raw_data);
}

bool CommManager::send_message_to_gateway(const Message &message, CommChannel comm_channel) {
    uint8_t tx_buffer[MessageTools::MAX_ENCODED_SIZE + 2];
    uint16_t crc;
    uint16_t num_bytes_encoded = MessageTools::encode_message_and_calc_crc(message, tx_buffer, MessageTools::MAX_ENCODED_SIZE, crc);
    if (!num_bytes_encoded) return false;
    memcpy(tx_buffer + num_bytes_encoded, &crc, sizeof(uint16_t));
    if (comm_channel == CommChannel::USB_SERIAL) {
        return serial_manager.send_message_to_host(message, tx_buffer, num_bytes_encoded + sizeof(uint16_t));
    }
    bool result = false;
    if (has_gateway()) {
        result = active_uplink_channel->send_message_to_gateway(message, tx_buffer, num_bytes_encoded + sizeof(uint16_t));
    }
    return result;
}

bool CommManager::send_message_to_host(const Message &message) {
    uint8_t tx_buffer[MessageTools::MAX_ENCODED_SIZE + 2];
    uint16_t crc;
    uint16_t num_bytes_encoded = MessageTools::encode_message_and_calc_crc(message, tx_buffer, MessageTools::MAX_ENCODED_SIZE, crc);
    memcpy(tx_buffer + num_bytes_encoded, &crc, sizeof(uint16_t));
    if (!num_bytes_encoded) return false;
    return serial_manager.send_message_to_host(message, tx_buffer, num_bytes_encoded + sizeof(uint16_t));
}

AxisID CommManager::get_axis_id(void) {
    return _config_manager->get_axis_id();
}

GatewayID CommManager::get_gateway_id(void) {
    return _config_manager->get_gateway_id();
}

void CommManager::on_ffb_action(const FFBAction &ffb_action) {
    if (ffb_action.function_id != _config_manager->get_function_id()) return;
    if (_on_ffb_action) {
        _on_ffb_action(ffb_action);
    }
}

bool CommManager::calc_controller_output_value(FunctionBase &function_base, float &controller_output) {
    AxisID primary_axis_id = AxisID(function_base.linked_axes[0] & AxisID_AXIS_ID_MASK);
    float src_value;
    bool success = false;
    switch (function_base.output_mode) {
        case OutputMode_OUTPUT_MODE_FORCE:
            success = get_force(primary_axis_id, src_value);
            break;
        case OutputMode_OUTPUT_MODE_TRAVEL:
            success = get_position(primary_axis_id, src_value);
            break;
        default:
            break;
    }
    if (!success) return false;
    controller_output = normalize_value(src_value, function_base.output_min, function_base.output_max);
    return true;
}

void CommManager::set_controller_axis(ControllerAxis controller_axis, float &value) {
    uint16_t final_value = uint16_t(value * float(JOYSTICK_MAX));
    switch (controller_axis) {
        case ControllerAxis_CONTROLLER_AXIS_X:
            _joystick.setXAxis(final_value);
            break;
        case ControllerAxis_CONTROLLER_AXIS_Y:
            _joystick.setYAxis(final_value);
            break;
        case ControllerAxis_CONTROLLER_AXIS_Z:
            _joystick.setZAxis(final_value);
            break;
        case ControllerAxis_CONTROLLER_AXIS_R_X:
            _joystick.setRxAxis(final_value);
            break;
        case ControllerAxis_CONTROLLER_AXIS_R_Y:
            _joystick.setRyAxis(final_value);
            break;
        case ControllerAxis_CONTROLLER_AXIS_R_Z:
            _joystick.setRzAxis(final_value);
            break;
        case ControllerAxis_CONTROLLER_AXIS_RUD:
            _joystick.setRudder(final_value);
            break;
        case ControllerAxis_CONTROLLER_AXIS_THR:
            _joystick.setThrottle(final_value);
            break;
        case ControllerAxis_CONTROLLER_AXIS_ACC:
            _joystick.setAccelerator(final_value);
            break;
        case ControllerAxis_CONTROLLER_AXIS_BRK:
            _joystick.setBrake(final_value);
            break;
        case ControllerAxis_CONTROLLER_AXIS_STEER:
            _joystick.setSteering(final_value);
            break;
        default:
            break;
    }
}

void CommManager::send_joystick_values(void) {
    if (_joystick_state != JOYSTICK_READY) return;
    uint32_t function_flags = 0;
    FunctionID function_id;
    for (uint8_t axis_idx = 0; axis_idx < MessageTools::MAX_AXES_COUNT; axis_idx++) {
        if (get_function_id(MessageTools::axis_id_from_index(axis_idx), function_id)) {
            if (function_id < 32) {
                function_flags |= 1u << function_id;
            }
        }
    }
    float output_value;
    memset(controller_axis_values, 0, sizeof(controller_axis_values));
    memset(controller_button_values, 0, sizeof(controller_button_values));
    for (uint8_t function_idx = 0; function_idx < _FunctionID_MAX; function_idx++) {
        function_id = FunctionID(function_idx + 1);
        if (function_flags & (1 << function_id)) {
            FunctionBase *function_base = _config_manager->get_function_base(function_id);
            if (function_base && function_base->controller_output_axis) {
                if (calc_controller_output_value(*function_base, output_value)) {
                    if (MessageTools::check_controller_axis_id(function_base->controller_output_axis)) {
                        controller_axis_values[MessageTools::controller_axis_index_from_id(function_base->controller_output_axis)] = output_value;
                    }
                }
            }
        }
    }
    for (uint8_t function_idx = 0; function_idx < _FunctionID_MAX; function_idx++) {
        function_id = FunctionID(function_idx + 1);
        if (function_flags & (1 << function_id)) {
            auto result = _config_manager->get_aux_function(function_id);
            if (std::get<0>(result)) {
                std::get<0>(result)->process(*this, std::get<1>(result));
            }
        }
    }
    for (uint8_t controller_axis_idx = 0; controller_axis_idx < _ControllerAxis_MAX; controller_axis_idx++) {
        ControllerAxis controller_axis = MessageTools::controller_axis_id_from_index(controller_axis_idx);
        set_controller_axis(controller_axis, controller_axis_values[controller_axis_idx]);
    }
    // Read grip shift registers into the upper 24 buttons (indices 24–47).
    // Lower indices (0–23) are reserved for function outputs (e.g., shifter gears).
    if (_grip_reader && _grip_reader->isReady()) {
        _grip_reader->poll();
        for (uint8_t bit = 0; bit < _grip_reader->getNumBits(); bit++) {
            uint8_t buttonIdx = 24 + bit;
            if (buttonIdx < JOYSTICK_BUTTON_COUNT) {
                controller_button_values[buttonIdx] = _grip_reader->isPressed(bit) ? 1 : 0;
            }
        }
    }

    for (uint8_t button_idx = 0; button_idx < CommManager::JOYSTICK_BUTTON_COUNT; button_idx++) {
        _joystick.setButton(button_idx, controller_button_values[button_idx]);
    }
    _joystick.sendState();
}

bool CommManager::calc_input_force_sum(const AxisID *linked_axes, float &input_force) {
    float f_sum = 0.0f;
    float temp;
    bool is_subtractive_axis = false;
    AxisID own_axis_id = get_axis_id();
    bool own_independent = false;
    for (uint8_t idx = 0; idx < (sizeof(FunctionBase::linked_axes) / sizeof(FunctionBase::linked_axes[0])); idx++) {
        AxisID axis_id = AxisID(linked_axes[idx] & AxisID_AXIS_ID_MASK);
        if (axis_id == AxisID_AXIS_UNDEFINED) break;
        if (axis_id == own_axis_id && (linked_axes[idx] & AxisID_AXIS_INDEPENDENT)) {
            own_independent = true;
            break;
        }
    }
    for (uint8_t idx = 0; idx < (sizeof(FunctionBase::linked_axes) / sizeof(FunctionBase::linked_axes[0])); idx++) {
        AxisID axis_id = AxisID(linked_axes[idx] & AxisID_AXIS_ID_MASK);
        if (axis_id == AxisID_AXIS_UNDEFINED) break;
        bool entry_independent = (linked_axes[idx] & AxisID_AXIS_INDEPENDENT);
        if (own_independent) {
            if (axis_id != own_axis_id) continue;
        } else if (entry_independent && axis_id != own_axis_id) {
            continue;
        }
        temp = 0.0f;
        get_force(axis_id, temp);  // get_force won't touch temp if the associated axis is not online, no need to check the return value
        if (linked_axes[idx] & AxisID_AXIS_SUBTRACTIVE) {
            if (axis_id == own_axis_id) is_subtractive_axis = true;
            f_sum -= temp;
        } else {
            f_sum += temp;
        }
    }
    input_force = f_sum;
    return is_subtractive_axis;
}

bool CommManager::calc_input_force_sum(float &input_force) {
    // Topology is precomputed in ConfigManager::update_topology_cache(). The
    // walk over linked_axes happened at config-update time; the hot path just
    // sums the cached fetch list.
    float f_sum = 0.0f;
    uint8_t n = _config_manager->get_force_fetch_count();
    for (uint8_t i = 0; i < n; i++) {
        const ForceFetchEntry &entry = _config_manager->get_force_fetch_entry(i);
        float temp = 0.0f;
        get_force(entry.axis_id, temp);  // get_force won't touch temp if the associated axis is not online
        f_sum += entry.sign * temp;
    }
    input_force = f_sum;
    return _config_manager->is_subtractive_axis();
}

bool CommManager::calc_final_position(float own_position, float &final_position) {
    switch (_config_manager->get_position_mode()) {
        case POSITION_MODE_USE_OWN:
            final_position = own_position;
            return true;
        case POSITION_MODE_FETCH_PRIMARY: {
            float other;
            if (!get_position(_config_manager->get_primary_axis_for_fetch(), other)) return false;
            final_position = other;
            return true;
        }
        case POSITION_MODE_FETCH_PRIMARY_MIRRORED: {
            float other;
            if (!get_position(_config_manager->get_primary_axis_for_fetch(), other)) return false;
            final_position = _config_manager->get_x_contact_point_center_2x() - other;
            return true;
        }
        case POSITION_MODE_NOT_MEMBER:
            return false;
    }
    return false;
}
