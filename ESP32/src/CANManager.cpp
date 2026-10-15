#include <CANManager.h>
#include <LogOutput.h>
#include <math.h>

/*****************************************************************************************************************/
/* isotp-c shim functions */
/*****************************************************************************************************************/
namespace {
constexpr uint32_t k_isotp_log_interval_us = 500000;
uint32_t ti_last_isotp_tx_log = 0;
uint32_t ti_last_isotp_rx_log = 0;

const char *isotp_result_label(int ret) {
    switch (ret) {
        case ISOTP_RET_OK:
            return "OK";
        case ISOTP_RET_ERROR:
            return "ERROR";
        case ISOTP_RET_INPROGRESS:
            return "INPROGRESS";
        case ISOTP_RET_OVERFLOW:
            return "OVERFLOW";
        case ISOTP_RET_WRONG_SN:
            return "WRONG_SN";
        case ISOTP_RET_NO_DATA:
            return "NO_DATA";
        case ISOTP_RET_TIMEOUT:
            return "TIMEOUT";
        case ISOTP_RET_LENGTH:
            return "LENGTH";
        case ISOTP_RET_NOSPACE:
            return "NOSPACE";
        default:
            return "UNKNOWN";
    }
}

bool should_log_isotp_error(int ret) {
    return (ret != ISOTP_RET_OK) && (ret != ISOTP_RET_INPROGRESS) && (ret != ISOTP_RET_NO_DATA);
}

bool should_log_isotp(uint32_t &last_log_time) {
    uint32_t now = micros();
    if ((now - last_log_time) > k_isotp_log_interval_us) {
        last_log_time = now;
        return true;
    }
    return false;
}

void log_isotp_error(const char *context, int ret, uint32_t len, AxisID axis_id, uint32_t &last_log_time) {
    if (!should_log_isotp_error(ret)) return;
    if (!should_log_isotp(last_log_time)) return;
    if (axis_id != AxisID_AXIS_UNDEFINED) {
        LogOutput::printf("CAN ISOTP %s: axis %d ret %d (%s) len %u", context, axis_id, ret, isotp_result_label(ret), len);
    } else {
        LogOutput::printf("CAN ISOTP %s: ret %d (%s) len %u", context, ret, isotp_result_label(ret), len);
    }
}
}  // namespace

extern "C" int isotp_user_send_can(const uint32_t id, const uint8_t *data, const uint8_t size) {
    CanFrame tx_frame = {};
    tx_frame.identifier = id;
    memcpy(tx_frame.data, data, size);
    tx_frame.data_length_code = size;
    if (!ESP32Can.writeFrame(&tx_frame, 0)) {
        return ISOTP_RET_NOSPACE;
    }
    return ISOTP_RET_OK;
}

extern "C" uint32_t isotp_user_get_us(void) {
    return micros();
}

extern "C" void isotp_user_debug(const char *message, ...) {
    LogOutput::printf(message);
}

/*****************************************************************************************************************/
/* CANManager */
namespace {
    constexpr float kFfbScaleSpring = 0.01f;
    constexpr float kFfbScaleDamper = 0.01f;
    constexpr float kFfbScaleTrim = 0.1f;
    constexpr float kFfbScaleBuffet = 0.01f;
    constexpr float kFfbScaleLoad = 0.1f;
    constexpr float kFfbScaleFriction = 0.1f;

    struct FlightFfbPayload {
        uint16_t k_spring;
        uint16_t k_damper;
        int16_t trim_offset;
        int16_t buffet_amp;
    };

    struct FlightFfbLoadPayload {
        int16_t load_force;
        uint16_t k_friction;
    };

    int16_t clamp_ffb_i16(float value, float scale) {
        int32_t scaled = (int32_t)lroundf(value / scale);
        if (scaled > INT16_MAX) {
            return INT16_MAX;
        }
        if (scaled < INT16_MIN) {
            return INT16_MIN;
        }
        return (int16_t)scaled;
    }

    uint16_t clamp_ffb_u16(float value, float scale) {
        int32_t scaled = (int32_t)lroundf(value / scale);
        if (scaled < 0) {
            return 0;
        }
        if (scaled > UINT16_MAX) {
            return UINT16_MAX;
        }
        return (uint16_t)scaled;
    }

    FlightFfbPayload pack_flight_ffb(const FlightFfbAction &action) {
        FlightFfbPayload payload = {};
        payload.k_spring = clamp_ffb_u16(action.k_spring, kFfbScaleSpring);
        payload.k_damper = clamp_ffb_u16(action.k_damper, kFfbScaleDamper);
        payload.trim_offset = clamp_ffb_i16(action.trim_offset, kFfbScaleTrim);
        payload.buffet_amp = clamp_ffb_i16(action.buffet_amp, kFfbScaleBuffet);
        return payload;
    }

    FlightFfbLoadPayload pack_flight_ffb_load(const FlightFfbAction &action) {
        FlightFfbLoadPayload payload = {};
        payload.load_force = clamp_ffb_i16(action.load_force, kFfbScaleLoad);
        payload.k_friction = clamp_ffb_u16(action.k_friction, kFfbScaleFriction);
        return payload;
    }

    FlightFfbAction unpack_flight_ffb(const FlightFfbPayload &payload) {
        FlightFfbAction action = FlightFfbAction_init_default;
        action.k_spring = payload.k_spring * kFfbScaleSpring;
        action.k_damper = payload.k_damper * kFfbScaleDamper;
        action.trim_offset = payload.trim_offset * kFfbScaleTrim;
        action.buffet_amp = payload.buffet_amp * kFfbScaleBuffet;
        return action;
    }

    void unpack_flight_ffb_load(const FlightFfbLoadPayload &payload, float &load_force, float &k_friction) {
        load_force = payload.load_force * kFfbScaleLoad;
        k_friction = payload.k_friction * kFfbScaleFriction;
    }

    // FLIGHT_VIB CAN frame: 5 DDS1 + 2 DDS2 amps, raw 0.05 N/LSB.
    // 7 bytes used, 1 byte spare in the 8-byte CAN frame.
    struct FlightFfbVibPayload {
        uint8_t vib_amps[5];
        uint8_t vib2_amps[2];
    };
    static_assert(sizeof(FlightFfbVibPayload) == 7, "FLIGHT_VIB payload must be 7 bytes");

    FlightFfbVibPayload pack_flight_ffb_vib(const FlightFfbAction &action) {
        FlightFfbVibPayload payload = {};
        // Proto fields are uint32 (with int_size:IS_8 → uint8 storage).
        // Plugin pre-scales floats × 20 and clamps to 0..255 before sending.
        payload.vib_amps[0] = (uint8_t)(action.vib_amp_slot1 & 0xFF);
        payload.vib_amps[1] = (uint8_t)(action.vib_amp_slot2 & 0xFF);
        payload.vib_amps[2] = (uint8_t)(action.vib_amp_slot3 & 0xFF);
        payload.vib_amps[3] = (uint8_t)(action.vib_amp_slot4 & 0xFF);
        payload.vib_amps[4] = (uint8_t)(action.vib_amp_slot5 & 0xFF);
        payload.vib2_amps[0] = (uint8_t)(action.vib2_amp_slot1 & 0xFF);
        payload.vib2_amps[1] = (uint8_t)(action.vib2_amp_slot2 & 0xFF);
        return payload;
    }

    constexpr uint32_t kDdsSyncCanId = 0x0F0;
    constexpr float kDdsHzScale = 0.001f;  // 0.001 Hz/LSB → 0..65.535 Hz range
    constexpr float kTwoPi = 2.0f * (float)M_PI;
    constexpr float kDdsPhasePackScale = 65536.0f / kTwoPi;
    constexpr float kDdsPhaseUnpackScale = kTwoPi / 65536.0f;

    // 8 bytes — fits one CAN frame exactly. Both ESP32 ends are little-endian
    // so memcpy works; spec section 4 byte order matches naturally.
    struct DdsSyncPayload {
        uint16_t dds1_hz;
        uint16_t dds1_phase;
        uint16_t dds2_hz;
        uint16_t dds2_phase;
    };
    static_assert(sizeof(DdsSyncPayload) == 8, "0x0F0 payload must be 8 bytes");

    uint16_t pack_dds_phase(float phase_rad) {
        // Wrap to [0, 2π). fmodf is O(1); the old iterative subtraction spins
        // forever on a non-finite or large phase (same hazard fixed in SyncVib
        // and MasterDds).
        if (!isfinite(phase_rad)) phase_rad = 0.0f;
        phase_rad = fmodf(phase_rad, kTwoPi);
        if (phase_rad < 0.0f) phase_rad += kTwoPi;
        int32_t scaled = (int32_t)lroundf(phase_rad * kDdsPhasePackScale);
        return (uint16_t)(scaled & 0xFFFF);
    }

    float unpack_dds_phase(uint16_t raw) {
        return raw * kDdsPhaseUnpackScale;
    }
}
/*****************************************************************************************************************/
bool CANManager::get_force(AxisID axis_id, float &f_contact_point) {
    if (!MessageTools::check_axis_id(axis_id)) return false;
    uint8_t axis_idx = MessageTools::axis_index_from_id(axis_id);
    if (axis_states[axis_idx].online) {
        f_contact_point = axis_states[axis_idx].force_and_position.f_contact_point;
        return true;
    }
    return false;
}

bool CANManager::get_position(AxisID axis_id, float &x_contact_point) {
    if (!MessageTools::check_axis_id(axis_id)) return false;
    uint8_t axis_idx = MessageTools::axis_index_from_id(axis_id);
    if (axis_states[axis_idx].online) {
        x_contact_point = axis_states[axis_idx].force_and_position.x_contact_point;
        return true;
    }
    return false;
}

bool CANManager::get_position_limits(AxisID axis_id, float &x_contact_point_min, float &x_contact_point_max) {
    if (!MessageTools::check_axis_id(axis_id)) return false;
    uint8_t axis_idx = MessageTools::axis_index_from_id(axis_id);
    if (axis_states[axis_idx].online) {
        x_contact_point_min = axis_states[axis_idx].position_limits.x_contact_point_min;
        x_contact_point_max = axis_states[axis_idx].position_limits.x_contact_point_max;
        return true;
    }
    return false;
}

bool CANManager::get_function_id(AxisID axis_id, FunctionID &function_id) {
    if (!MessageTools::check_axis_id(axis_id)) return false;
    uint8_t axis_idx = MessageTools::axis_index_from_id(axis_id);
    if (axis_states[axis_idx].online) {
        function_id = axis_states[axis_idx].function_id;
        return true;
    }
    return false;
}

bool CANManager::is_online(AxisID axis_id) {
    if (!MessageTools::check_axis_id(axis_id)) return false;
    return axis_states[MessageTools::axis_index_from_id(axis_id)].online;
}

bool CANManager::check_bus(uint32_t ti_now) {
    auto can_state = ESP32Can.canState();
    switch (bus_state) {
        case BusState::ONLINE:
            if (can_state == TWAI_STATE_BUS_OFF) {
                switch_bus_state(ti_now, BusState::BUS_OFF);
                LogOutput::printf("CANManager: BUS_OFF");
            }
            break;
        case BusState::BUS_OFF:
            if ((ti_now - ti_state_change) > 1000000) {
                switch_bus_state(ti_now, BusState::PRE_ONLINE);
                ESP32Can.recover();
            }
            break;
        case BusState::PRE_ONLINE:
            if (can_state == TWAI_STATE_BUS_OFF) {
                switch_bus_state(ti_now, BusState::BUS_OFF);
            } else if ((ti_now - ti_state_change) > 100000) {
                if (can_state != TWAI_STATE_RUNNING) {
                    ti_state_change = ti_now;
                    ESP32Can.restart();
                } else {
                    switch_bus_state(ti_now, BusState::ONLINE);
                    LogOutput::printf("CANManager: Online");
                }
            }
            break;
        default:
            switch_bus_state(ti_now, BusState::PRE_ONLINE);
            LogOutput::printf("CANManager: Error: Unknown BusState (0x%02X)!", bus_state);
            break;
    }
    return bus_state == BusState::ONLINE;
}

bool CANManager::try_process_high_prio_axis_frame(CanFrame &rx_frame, uint32_t now) {
    if ((rx_frame.identifier & 0xF00) == 0x100) {
        uint8_t axis_idx = rx_frame.identifier & 0x00F;
        uint8_t frame_type = (rx_frame.identifier >> 4) & 0x00F;
        on_axis_seen(axis_idx, now, true);
        switch (frame_type) {
            case AxisFrameTypesHS::FORCE_AND_POSITION:
                if (axis_idx < MessageTools::MAX_AXES_COUNT) {
                    memcpy(&(axis_states[axis_idx].force_and_position), rx_frame.data, sizeof(ForceAndPosition));
                }
                break;
            default:
                break;
        }
        return true;
    }
    return false;
}

void CANManager::on_axis_seen(uint8_t axis_idx, uint32_t now, bool from_high_prio_frame) {
    axis_states[axis_idx].ti_last_seen = now;
    if (from_high_prio_frame) {
        axis_states[axis_idx].ti_timeout = 20000;
    }
    if (!axis_states[axis_idx].online) {
        axis_states[axis_idx].online = true;
        if (!from_high_prio_frame) {
            axis_states[axis_idx].ti_timeout = 2000000;
        }
        if (on_axis_state_change) {
            on_axis_state_change(MessageTools::axis_id_from_index(axis_idx), true);
        }
    }
}

bool CANManager::try_process_low_prio_axis_frame(CanFrame &rx_frame, uint32_t now) {
    if ((rx_frame.identifier & 0xF00) == 0x300) {
        uint8_t axis_idx = rx_frame.identifier & 0x00F;
        uint8_t frame_type = (rx_frame.identifier >> 4) & 0x00F;
        on_axis_seen(axis_idx, now);
        axis_states[axis_idx].ti_last_status_update = now;
        axis_states[axis_idx].status_valid = true;
        switch (frame_type) {
            case AxisFrameTypesLS::POSITION_LIMITS:
                if (axis_idx < MessageTools::MAX_AXES_COUNT) {
                    memcpy(&(axis_states[axis_idx].position_limits), rx_frame.data, sizeof(PositionLimits));
                }
                break;
            case AxisFrameTypesLS::FUNCTION_ID:
                if (axis_idx < MessageTools::MAX_AXES_COUNT) {
                    memcpy(&(axis_states[axis_idx].function_id), rx_frame.data, sizeof(FunctionID));
                }
                break;
            default:
                break;
        }
        return true;
    }
    return false;
}

void CANManager::process(void) {
    CanFrame rx_frame;
    uint8_t num_max_frames = 40;
    uint32_t now = micros();
    update_timeouts(now);
    broadcast_state_updates(now);
    if (check_bus(now) == false) {
        return;
    }
    while (num_max_frames--) {
        if (ESP32Can.readFrame(&rx_frame, 0)) {
            if (try_process_high_prio_axis_frame(rx_frame, now)) continue;
            if (!_is_gateway) {
                if (try_process_dds_sync_frame(rx_frame, now)) continue;
                if (try_process_ffb_update_frame(rx_frame)) continue;
            }
            if (try_process_low_prio_axis_frame(rx_frame, now)) continue;
            if (try_process_gateway_isotp_can_frame(rx_frame)) continue;
            if (try_process_ping_frame(rx_frame, now)) continue;
            if (_is_gateway) {
                if (try_process_axis_isotp_can_frame(rx_frame)) continue;
            }
        } else {
            return;
        }
    }
    if (rx_err_cnt < 0xFFFFFFFF) {
        rx_err_cnt++;
    }
}

void CANManager::process_isotp(void) {
    IsoTpLink *link;
    int ret;
    if (!_is_gateway) {
        link = &(gateway_isotp_state.link);
        isotp_poll(link);
        ret = isotp_receive(link, isotp_rx_buff, ISOTP_BUFFER_SIZE, &isotp_rx_size);
        if (ret == ISOTP_RET_OK) {
            if (on_gateway_payload) {
                on_gateway_payload(isotp_rx_buff, isotp_rx_size);
            }
        } else {
            log_isotp_error("rx gateway", ret, ISOTP_BUFFER_SIZE, AxisID_AXIS_UNDEFINED, ti_last_isotp_rx_log);
        }
        link = &(outbound_logging_isotp_state.link);
        isotp_poll(link);
        ret = isotp_receive(link, isotp_rx_buff, ISOTP_BUFFER_SIZE, &isotp_rx_size);
        if (ret == ISOTP_RET_OK) {
            if ((isotp_rx_size == 1) && (isotp_rx_buff[0] == 0xAA)) {
                _last_log_ack_received = true;
            }
        } else {
            log_isotp_error("rx log ack", ret, ISOTP_BUFFER_SIZE, AxisID_AXIS_UNDEFINED, ti_last_isotp_rx_log);
        }
    } else {
        send_ping_frame(micros());
        for (int axis_idx = 0; axis_idx < MessageTools::MAX_AXES_COUNT; axis_idx++) {
            link = &(isotp_state[axis_idx].link);
            isotp_poll(link);
            ret = isotp_receive(link, isotp_rx_buff, ISOTP_BUFFER_SIZE, &isotp_rx_size);
            if (ret == ISOTP_RET_OK) {
                if (on_axis_payload) {
                    on_axis_payload(MessageTools::axis_id_from_index(axis_idx), isotp_rx_buff, isotp_rx_size);
                }
            } else {
                log_isotp_error("rx axis", ret, ISOTP_BUFFER_SIZE, MessageTools::axis_id_from_index(axis_idx), ti_last_isotp_rx_log);
            }
            link = &(inbound_logging_isotp_states[axis_idx].link);
            isotp_poll(link);
            ret = isotp_receive(link, isotp_rx_buff, ISOTP_BUFFER_SIZE, &isotp_rx_size);
            if (ret == ISOTP_RET_OK) {
                if (on_axis_payload) {
                    on_axis_payload(MessageTools::axis_id_from_index(axis_idx), isotp_rx_buff, isotp_rx_size);
                }
                uint8_t token = 0xAA;
                int ack_ret = isotp_send(link, &token, sizeof(token));
                if (ack_ret != ISOTP_RET_OK) {
                    log_isotp_error("tx log ack", ack_ret, sizeof(token), MessageTools::axis_id_from_index(axis_idx), ti_last_isotp_tx_log);
                }
            } else {
                log_isotp_error("rx log", ret, ISOTP_BUFFER_SIZE, MessageTools::axis_id_from_index(axis_idx), ti_last_isotp_rx_log);
            }
        }
    }
}

void CANManager::update_timeouts(uint32_t now) {
    for (int axis_idx = 0; axis_idx < MessageTools::MAX_AXES_COUNT; axis_idx++) {
        if (axis_states[axis_idx].online && ((now - axis_states[axis_idx].ti_last_seen) > axis_states[axis_idx].ti_timeout)) {
            if (axis_states[axis_idx].online) {
                axis_states[axis_idx].online = false;
                if (on_axis_state_change) {
                    on_axis_state_change(MessageTools::axis_id_from_index(axis_idx), false);
                }
                axis_states[axis_idx].ti_timeout = 2000000;
            }
        }
        if (axis_states[axis_idx].status_valid && ((now - axis_states[axis_idx].ti_last_status_update) > 5000000)) {
            axis_states[axis_idx].status_valid = false;
        }
    }
    if (!_is_gateway) {
        if (_gateway_online && ((now - ti_last_ping) > 200000)) {
            LogOutput::printf("CANManager: Gateway offline");
            _gateway_online = false;
            if (on_gateway_state_change) {
                on_gateway_state_change(this, false);
            }
        }
        if (!_last_log_ack_received) {
            if ((now - ti_last_log_sent) > 100000) {
                _last_log_ack_received = true;
                LogOutput::printf("CANManager: log ACK not received");
            }
        }
    }
}

void CANManager::shared_setup(uint16_t baud_rate, int8_t tx_pin, int8_t rx_pin) {
    // Explicit bit timing instead of TWAI_TIMING_CONFIG_*(), because the macro's
    // value changed underneath us on the IDF 5.1 -> 5.5 move and silently halved
    // the timing margin at 1 Mbit:
    //
    //   IDF 5.1 (ESP32-S3): brp=4  -> 20 TQ/bit, tseg 15/4, sjw=3, 80% sample
    //   IDF 5.5 (ESP32-S3):          8 TQ/bit, tseg  5/2, sjw=1, 75% sample
    //
    // 8 TQ with SJW=1 leaves almost nothing for transceiver loop delay and
    // oscillator tolerance, which showed up as a high bit-error rate on the axes
    // (TEC climbing to BUS_OFF, bus_err in the hundreds per second) on otherwise
    // unchanged, correctly terminated wiring with millimetre stubs.
    //
    // Pinning it here also keeps nodes on older firmware interoperable, since
    // this restores exactly what they use, and makes the bus immune to the macro
    // changing again on a future core bump.
    twai_timing_config_t t_config = {};
    t_config.clk_src = TWAI_CLK_SRC_DEFAULT;
    t_config.quanta_resolution_hz = (uint32_t)baud_rate * 1000 * 20;  // 20 TQ per bit
    t_config.brp = 0;                                                 // derived from quanta_resolution_hz
    t_config.prop_seg = 0;
    t_config.tseg_1 = 15;
    t_config.tseg_2 = 4;
    t_config.sjw = 3;
    t_config.ssp_offset = 0;
    t_config.triple_sampling = false;
    ESP32Can.begin(ESP32Can.convertSpeed(baud_rate), tx_pin, rx_pin, 40, 40, nullptr, nullptr, &t_config);
    isotp_init_link(&(gateway_isotp_state.link), 0x710 + own_axis_index, gateway_isotp_state.isotp_link_tx_buff, sizeof(IsotpState::isotp_link_tx_buff),
                    gateway_isotp_state.isotp_link_rx_buff, sizeof(IsotpState::isotp_link_rx_buff));
    isotp_init_link(&(outbound_logging_isotp_state.link), 0x730 + own_axis_index, outbound_logging_isotp_state.isotp_link_tx_buff, sizeof(IsotpStateOutboundLogging::isotp_link_tx_buff),
                    outbound_logging_isotp_state.isotp_link_rx_buff, sizeof(IsotpStateOutboundLogging::isotp_link_rx_buff));
    for (int i = 0; i < MessageTools::MAX_AXES_COUNT; i++) {
        isotp_init_link(&(isotp_state[i].link), 0x700 + i, isotp_state[i].isotp_link_tx_buff, sizeof(IsotpState::isotp_link_tx_buff), isotp_state[i].isotp_link_rx_buff,
                        sizeof(IsotpState::isotp_link_rx_buff));
        isotp_init_link(&(inbound_logging_isotp_states[i].link), 0x720 + i, inbound_logging_isotp_states[i].isotp_link_tx_buff, sizeof(IsotpStateInboundLogging::isotp_link_tx_buff), inbound_logging_isotp_states[i].isotp_link_rx_buff,
                        sizeof(IsotpStateInboundLogging::isotp_link_rx_buff));
    }
    switch_bus_state(BusState::ONLINE);
}

/*****************************************************************************************************************/
/* AxisCANManager */
/*****************************************************************************************************************/
void CANManager::mark_gateway_alive(uint32_t now) {
    ti_last_ping = now;
    if (!_gateway_online) {
        LogOutput::printf("CANManager: Gateway online");
        _gateway_online = true;
        if (own_axis_index > 0) {
            _is_gateway = false;
            LogOutput::printf(" -> giving up Gateway role");
        }
        if (on_gateway_state_change) {
            on_gateway_state_change(this, true);
        }
    }
}

bool CANManager::try_process_ping_frame(CanFrame &rx_frame, uint32_t now) {
    if (rx_frame.identifier == 0x7FE) {
        mark_gateway_alive(now);
        return true;
    }
    return false;
}

void CANManager::broadcast_state_updates(void) {
    if (own_axis_index < 0) return;
    if (_x_contact_point_min < _x_contact_point_max) {
        update_position_limits(_x_contact_point_min, _x_contact_point_max);
    }
    if (_function_id != FunctionID_FUNCTION_ID_UNDEFINED) {
        update_function_id(_function_id);
    }
}

void CANManager::broadcast_state_updates(uint32_t now) {
    if ((now - axis_states[own_axis_index].ti_last_status_update) > 2000000) {
        broadcast_state_updates();
    }
}

bool CANManager::setup(AxisID axis_id, uint16_t baud_rate, int8_t tx_pin, int8_t rx_pin, OnGatewayPayload on_gateway_payload,
                       OnFFBAction on_ffb_action, OnAxisPayload on_axis_payload, OnAxisStateChange on_axis_state_change,
                       OnGatewayStateChange on_gateway_state_change, OnDdsSync on_dds_sync) {
    LogOutput::printf("CANManager: Performing setup...");
    own_axis_id = axis_id;
    own_axis_index = MessageTools::axis_index_from_id(axis_id);
    this->on_gateway_payload = on_gateway_payload;
    this->on_ffb_action = on_ffb_action;
    this->on_axis_payload = on_axis_payload;
    this->on_axis_state_change = on_axis_state_change;
    this->on_gateway_state_change = on_gateway_state_change;
    this->on_dds_sync = on_dds_sync;
    shared_setup(baud_rate, tx_pin, rx_pin);
    broadcast_state_updates();
    LogOutput::printf(" -> done");
    return true;
}

bool CANManager::send_force_and_position(float &f_contact_point, float &x_contact_point) {
    if (own_axis_index < 0) return false;
    axis_states[own_axis_index].force_and_position.f_contact_point = f_contact_point;
    axis_states[own_axis_index].force_and_position.x_contact_point = x_contact_point;
    axis_states[own_axis_index].online = true;
    CanFrame tx_frame = {};
    tx_frame.identifier = 0x100 + (AxisFrameTypesHS::FORCE_AND_POSITION << 4) + own_axis_index;
    memcpy(tx_frame.data, &(axis_states[own_axis_index].force_and_position), sizeof(ForceAndPosition));
    tx_frame.data_length_code = sizeof(ForceAndPosition);
    if (!ESP32Can.writeFrame(&tx_frame, 0)) {
        if (tx_err_cnt < 0xFFFFFFFF) {
            tx_err_cnt++;
        }
        return false;
    }
    return true;
}

bool CANManager::try_process_dds_sync_frame(CanFrame &rx_frame, uint32_t now) {
    if (rx_frame.identifier != kDdsSyncCanId) return false;
    // Doubles as a gateway liveness signal. 0x7FE remains the primary ping
    // for now; this is a forward-compat fallback so the 10 Hz ping can be
    // retired in a later firmware version with no coordinated upgrade.
    mark_gateway_alive(now);
    if (rx_frame.data_length_code < sizeof(DdsSyncPayload)) return true;
    DdsSyncPayload payload;
    memcpy(&payload, rx_frame.data, sizeof(payload));
    if (on_dds_sync) {
        on_dds_sync(0, unpack_dds_phase(payload.dds1_phase), payload.dds1_hz * kDdsHzScale);
        on_dds_sync(1, unpack_dds_phase(payload.dds2_phase), payload.dds2_hz * kDdsHzScale);
    }
    return true;
}

bool CANManager::send_dds_sync(float dds1_hz, float dds1_phase,
                               float dds2_hz, float dds2_phase) {
    if (!_is_gateway) return false;
    DdsSyncPayload payload = {};
    payload.dds1_hz = clamp_ffb_u16(dds1_hz, kDdsHzScale);
    payload.dds1_phase = pack_dds_phase(dds1_phase);
    payload.dds2_hz = clamp_ffb_u16(dds2_hz, kDdsHzScale);
    payload.dds2_phase = pack_dds_phase(dds2_phase);
    CanFrame tx_frame = {};
    tx_frame.identifier = kDdsSyncCanId;
    tx_frame.data_length_code = sizeof(payload);
    memcpy(tx_frame.data, &payload, sizeof(payload));
    if (!ESP32Can.writeFrame(&tx_frame, 0)) {
        if (tx_err_cnt < 0xFFFFFFFF) {
            tx_err_cnt++;
        }
        return false;
    }
    return true;
}

bool CANManager::update_position_limits(float x_contact_point_min, float x_contact_point_max) {
    _x_contact_point_min = x_contact_point_min;
    _x_contact_point_max = x_contact_point_max;
    if (own_axis_index < 0) return false;
    axis_states[own_axis_index].position_limits.x_contact_point_min = x_contact_point_min;
    axis_states[own_axis_index].position_limits.x_contact_point_max = x_contact_point_max;
    axis_states[own_axis_index].ti_last_status_update = micros();
    axis_states[own_axis_index].status_valid = true;
    CanFrame tx_frame = {};
    tx_frame.identifier = 0x300 + (AxisFrameTypesLS::POSITION_LIMITS << 4) + own_axis_index;
    memcpy(tx_frame.data, &(axis_states[own_axis_index].position_limits), sizeof(PositionLimits));
    tx_frame.data_length_code = sizeof(PositionLimits);
    if (!ESP32Can.writeFrame(&tx_frame, 0)) {
        if (tx_err_cnt < 0xFFFFFFFF) {
            tx_err_cnt++;
        }
        return false;
    }
    return true;
}

bool CANManager::update_function_id(FunctionID function_id) {
    _function_id = function_id;
    if (own_axis_index < 0) return false;
    axis_states[own_axis_index].function_id = _function_id;
    axis_states[own_axis_index].ti_last_status_update = micros();
    axis_states[own_axis_index].status_valid = true;
    CanFrame tx_frame = {};
    tx_frame.identifier = 0x300 + (AxisFrameTypesLS::FUNCTION_ID << 4) + own_axis_index;
    memcpy(tx_frame.data, &(axis_states[own_axis_index].function_id), sizeof(FunctionID));
    tx_frame.data_length_code = sizeof(FunctionID);
    if (!ESP32Can.writeFrame(&tx_frame, 0)) {
        if (tx_err_cnt < 0xFFFFFFFF) {
            tx_err_cnt++;
        }
        return false;
    }
    return true;
}

bool CANManager::send_payload_to_gateway(const uint8_t *data, uint32_t len) {
    int ret = isotp_send(&(gateway_isotp_state.link), data, len);
    if (ret != ISOTP_RET_OK) {
        log_isotp_error("tx gateway", ret, len, AxisID_AXIS_UNDEFINED, ti_last_isotp_tx_log);
    }
    return ret == ISOTP_RET_OK;
}

bool CANManager::send_message_to_gateway(const Message &message, const uint8_t *raw_data, size_t len_raw_data) {
    if (message.which_payload == Message_axis_log_message_tag) {
        _last_log_ack_received = false;
        ti_last_log_sent = micros();
        int ret = isotp_send(&(outbound_logging_isotp_state.link), raw_data, len_raw_data);
        if (ret != ISOTP_RET_OK) {
            log_isotp_error("tx log", ret, len_raw_data, AxisID_AXIS_UNDEFINED, ti_last_isotp_tx_log);
        }
        return ret == ISOTP_RET_OK;
    } else {
        return send_payload_to_gateway(raw_data, len_raw_data);
    }
}

bool CANManager::ready_to_receive_log_message(void) {
    return _last_log_ack_received;
}

bool CANManager::try_process_gateway_isotp_can_frame(CanFrame &rx_frame) {
    if (own_axis_index < 0) return false;
    if (rx_frame.identifier == (0x700 + own_axis_index)) {
        isotp_on_can_message(&(gateway_isotp_state.link), rx_frame.data, rx_frame.data_length_code);
        return true;
    } else if (rx_frame.identifier == (0x720 + own_axis_index)) {
        isotp_on_can_message(&(outbound_logging_isotp_state.link), rx_frame.data, rx_frame.data_length_code);
        return true;
    }
    return false;
}

bool CANManager::try_process_ffb_update_frame(CanFrame &rx_frame) {
    if (own_axis_index < 0) return false;
    if ((rx_frame.identifier & 0xF00) == 0x200) {
        uint8_t function_id = rx_frame.identifier & 0x00F;
        uint8_t frame_type = (rx_frame.identifier >> 4) & 0x00F;
        FFBAction action = FFBAction_init_default;
        switch (frame_type) {
            case FFBFrameTypes::ABS:
                action.function_id = FunctionID(function_id);
                action.which_function = FFBAction_automotive_pedal_tag;
                action.function.automotive_pedal.trigger_abs = true;
                on_ffb_action(action);
                break;
            case FFBFrameTypes::FLIGHT_FFB: {
                if (function_id == 0 || function_id > MessageTools::MAX_AXES_COUNT) {
                    break;
                }
                if (rx_frame.data_length_code < sizeof(FlightFfbPayload)) {
                    break;
                }
                FlightFfbPayload payload = {};
                memcpy(&payload, rx_frame.data, sizeof(payload));
                FlightFfbCache &cache = flight_ffb_cache[function_id - 1];
                cache.base = unpack_flight_ffb(payload);
                cache.has_base = true;
                if (cache.has_load) {
                    cache.base.load_force = cache.load_force;
                    cache.base.k_friction = cache.k_friction;
                }
                if (cache.has_vib) {
                    cache.base.vib_amp_slot1 = cache.vib_amps[0];
                    cache.base.vib_amp_slot2 = cache.vib_amps[1];
                    cache.base.vib_amp_slot3 = cache.vib_amps[2];
                    cache.base.vib_amp_slot4 = cache.vib_amps[3];
                    cache.base.vib_amp_slot5 = cache.vib_amps[4];
                    cache.base.vib2_amp_slot1 = cache.vib2_amps[0];
                    cache.base.vib2_amp_slot2 = cache.vib2_amps[1];
                }
                action.function_id = FunctionID(function_id);
                action.which_function = FFBAction_flight_ffb_tag;
                action.function.flight_ffb = cache.base;
                on_ffb_action(action);
                break;
            }
            case FFBFrameTypes::FLIGHT_FFB_LOAD: {
                if (function_id == 0 || function_id > MessageTools::MAX_AXES_COUNT) {
                    break;
                }
                if (rx_frame.data_length_code < sizeof(FlightFfbLoadPayload)) {
                    break;
                }
                FlightFfbLoadPayload payload = {};
                memcpy(&payload, rx_frame.data, sizeof(payload));
                FlightFfbCache &cache = flight_ffb_cache[function_id - 1];
                unpack_flight_ffb_load(payload, cache.load_force, cache.k_friction);
                cache.has_load = true;
                if (!cache.has_base) {
                    break;
                }
                action.function_id = FunctionID(function_id);
                action.which_function = FFBAction_flight_ffb_tag;
                action.function.flight_ffb = cache.base;
                action.function.flight_ffb.load_force = cache.load_force;
                action.function.flight_ffb.k_friction = cache.k_friction;
                if (cache.has_vib) {
                    action.function.flight_ffb.vib_amp_slot1 = cache.vib_amps[0];
                    action.function.flight_ffb.vib_amp_slot2 = cache.vib_amps[1];
                    action.function.flight_ffb.vib_amp_slot3 = cache.vib_amps[2];
                    action.function.flight_ffb.vib_amp_slot4 = cache.vib_amps[3];
                    action.function.flight_ffb.vib_amp_slot5 = cache.vib_amps[4];
                    action.function.flight_ffb.vib2_amp_slot1 = cache.vib2_amps[0];
                    action.function.flight_ffb.vib2_amp_slot2 = cache.vib2_amps[1];
                }
                on_ffb_action(action);
                break;
            }
            case FFBFrameTypes::FLIGHT_VIB: {
                if (function_id == 0 || function_id > MessageTools::MAX_AXES_COUNT) {
                    break;
                }
                if (rx_frame.data_length_code < sizeof(FlightFfbVibPayload)) {
                    break;
                }
                FlightFfbVibPayload payload = {};
                memcpy(&payload, rx_frame.data, sizeof(payload));
                FlightFfbCache &cache = flight_ffb_cache[function_id - 1];
                memcpy(cache.vib_amps, payload.vib_amps, sizeof(cache.vib_amps));
                memcpy(cache.vib2_amps, payload.vib2_amps, sizeof(cache.vib2_amps));
                cache.has_vib = true;
                if (!cache.has_base) {
                    break;
                }
                action.function_id = FunctionID(function_id);
                action.which_function = FFBAction_flight_ffb_tag;
                action.function.flight_ffb = cache.base;
                if (cache.has_load) {
                    action.function.flight_ffb.load_force = cache.load_force;
                    action.function.flight_ffb.k_friction = cache.k_friction;
                }
                action.function.flight_ffb.vib_amp_slot1 = cache.vib_amps[0];
                action.function.flight_ffb.vib_amp_slot2 = cache.vib_amps[1];
                action.function.flight_ffb.vib_amp_slot3 = cache.vib_amps[2];
                action.function.flight_ffb.vib_amp_slot4 = cache.vib_amps[3];
                action.function.flight_ffb.vib_amp_slot5 = cache.vib_amps[4];
                action.function.flight_ffb.vib2_amp_slot1 = cache.vib2_amps[0];
                action.function.flight_ffb.vib2_amp_slot2 = cache.vib2_amps[1];
                on_ffb_action(action);
                break;
            }
            default:
                break;
        }
        return true;
    }
    return false;
}

/*****************************************************************************************************************/
/* GatewayCANManager */
/*****************************************************************************************************************/
void CANManager::send_ping_frame(uint32_t now) {
    if ((now - ti_last_ping) > 100000) {
        ti_last_ping = now;
        CanFrame tx_frame = {};
        tx_frame.identifier = 0x7FE;
        tx_frame.data_length_code = 0;
        if (!ESP32Can.writeFrame(&tx_frame, 0)) {
            if (tx_err_cnt < 0xFFFFFFFF) {
                tx_err_cnt++;
            }
        }
    }
}

bool CANManager::try_process_axis_isotp_can_frame(CanFrame &rx_frame) {
    if ((rx_frame.identifier & 0xFF0) == 0x710) {
        uint8_t axis_idx = rx_frame.identifier & 0x00F;
        if (axis_idx < MessageTools::MAX_AXES_COUNT) {
            on_axis_seen(axis_idx);
            isotp_on_can_message(&(isotp_state[axis_idx].link), rx_frame.data, rx_frame.data_length_code);
        }
        return true;
    } else if ((rx_frame.identifier & 0xFF0) == 0x730) {
        uint8_t axis_idx = rx_frame.identifier & 0x00F;
        if (axis_idx < MessageTools::MAX_AXES_COUNT) {
            on_axis_seen(axis_idx);
            isotp_on_can_message(&(inbound_logging_isotp_states[axis_idx].link), rx_frame.data, rx_frame.data_length_code);
        }
        return true;
    }

    return false;
}

bool CANManager::send_payload_to_axis(AxisID axis_id, const uint8_t *data, uint32_t len) {
    if (!MessageTools::check_axis_id(axis_id)) return false;
    int ret = isotp_send(&(isotp_state[MessageTools::axis_index_from_id(axis_id)].link), data, len);
    if (ret != ISOTP_RET_OK) {
        log_isotp_error("tx axis", ret, len, axis_id, ti_last_isotp_tx_log);
    }
    return ret == ISOTP_RET_OK;
}

bool CANManager::send_abs_trigger(const FFBAction &action) {
    CanFrame tx_frame = {};
    tx_frame.identifier = 0x200 + (FFBFrameTypes::ABS << 4) + action.function_id;
    tx_frame.data_length_code = 0;
    if (!ESP32Can.writeFrame(&tx_frame, 0)) {
        if (tx_err_cnt < 0xFFFFFFFF) {
            tx_err_cnt++;
        }
        return false;
    }
    return true;
}

bool CANManager::send_flight_ffb(const FFBAction &action) {
    if (action.which_function != FFBAction_flight_ffb_tag) {
        return false;
    }

    FlightFfbPayload payload = pack_flight_ffb(action.function.flight_ffb);
    FlightFfbLoadPayload load_payload = pack_flight_ffb_load(action.function.flight_ffb);
    CanFrame tx_frame = {};
    tx_frame.identifier = 0x200 + (FFBFrameTypes::FLIGHT_FFB << 4) + action.function_id;
    tx_frame.data_length_code = sizeof(payload);
    memcpy(tx_frame.data, &payload, sizeof(payload));
    bool base_ok = ESP32Can.writeFrame(&tx_frame, 0);
    if (!base_ok) {
        if (tx_err_cnt < 0xFFFFFFFF) {
            tx_err_cnt++;
        }
    }
    CanFrame load_frame = {};
    load_frame.identifier = 0x200 + (FFBFrameTypes::FLIGHT_FFB_LOAD << 4) + action.function_id;
    load_frame.data_length_code = sizeof(load_payload);
    memcpy(load_frame.data, &load_payload, sizeof(load_payload));
    bool load_ok = ESP32Can.writeFrame(&load_frame, 0);
    if (!load_ok) {
        if (tx_err_cnt < 0xFFFFFFFF) {
            tx_err_cnt++;
        }
    }
    FlightFfbVibPayload vib_payload = pack_flight_ffb_vib(action.function.flight_ffb);
    CanFrame vib_frame = {};
    vib_frame.identifier = 0x200 + (FFBFrameTypes::FLIGHT_VIB << 4) + action.function_id;
    vib_frame.data_length_code = sizeof(vib_payload);
    memcpy(vib_frame.data, &vib_payload, sizeof(vib_payload));
    bool vib_ok = ESP32Can.writeFrame(&vib_frame, 0);
    if (!vib_ok) {
        if (tx_err_cnt < 0xFFFFFFFF) {
            tx_err_cnt++;
        }
    }
    return base_ok && load_ok && vib_ok;
}

bool CANManager::send_message_to_axis(AxisID axis_id, const Message &message, const uint8_t *raw_data, size_t len_raw_data) {
    switch (message.which_payload) {
        case Message_ffb_action_tag:
            if ((message.payload.ffb_action.which_function == FFBAction_automotive_pedal_tag) &&
                message.payload.ffb_action.function.automotive_pedal.trigger_abs) {
                return send_abs_trigger(message.payload.ffb_action);
            } else if (message.payload.ffb_action.which_function == FFBAction_flight_ffb_tag) {
                return send_flight_ffb(message.payload.ffb_action);
            } else {
                return send_payload_to_axis(axis_id, raw_data, len_raw_data);
            }
            break;
        default:
            return send_payload_to_axis(axis_id, raw_data, len_raw_data);
            break;
    }
    return false;
}
