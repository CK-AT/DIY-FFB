#pragma once
#include <Arduino.h>
#include <MessageTools.h>
#include <isotp.h>

#include <ESP32-TWAI-CAN.hpp>

#include "CANManager.fwd.h"
#include "ICommChannel.h"

#define ISOTP_BUFFER_SIZE 512
#define ISOTP_LOG_BUFFER_SIZE 160 // AxisLogMessage_size + some Message overhead

class CANManager : public ICommChannel {
    protected:
        enum BusState {
            PRE_ONLINE,
            ONLINE,
            BUS_OFF
        };

        enum AxisFrameTypesHS {
            FORCE_AND_POSITION = 0
        };

        enum AxisFrameTypesLS {
            POSITION_LIMITS = 0,
            FUNCTION_ID = 1
        };

        enum FFBFrameTypes {
            ABS = 0,
            FLIGHT_FFB = 1,
            FLIGHT_FFB_LOAD = 2,
            FLIGHT_VIB = 3
        };

        struct ForceAndPosition {
                float f_contact_point;
                float x_contact_point;
        };

        struct PositionLimits {
                float x_contact_point_min;
                float x_contact_point_max;
        };

        struct AxisState {
                ForceAndPosition force_and_position;
                PositionLimits position_limits;
                FunctionID function_id;
                uint32_t ti_last_seen;
                uint32_t ti_last_status_update;
                uint32_t ti_timeout;
                bool online;
                bool status_valid;
        };

        struct FlightFfbCache {
                FlightFfbAction base;
                float load_force;
                float k_friction;
                uint8_t vib_amps[5];   // 0.01 N/LSB
                uint8_t vib2_amps[2];  // 0.01 N/LSB
                bool has_base;
                bool has_load;
                bool has_vib;
        };

        struct IsotpState {
                IsoTpLink link;
                uint8_t isotp_link_rx_buff[ISOTP_BUFFER_SIZE];
                uint8_t isotp_link_tx_buff[ISOTP_BUFFER_SIZE];
        };
        struct IsotpStateOutboundLogging {
                IsoTpLink link;
                uint8_t isotp_link_rx_buff[1];
                uint8_t isotp_link_tx_buff[ISOTP_LOG_BUFFER_SIZE];
        };
        struct IsotpStateInboundLogging {
                IsoTpLink link;
                uint8_t isotp_link_rx_buff[ISOTP_LOG_BUFFER_SIZE];
                uint8_t isotp_link_tx_buff[1];
        };

    public:
        bool get_force(AxisID axis_id, float &f_contact_point) override;
        bool get_position(AxisID axis_id, float &x_contact_point) override;
        bool get_position_limits(AxisID axis_id, float &x_contact_point_min, float &x_contact_point_max) override;
        bool get_function_id(AxisID axis_id, FunctionID &function_id) override;
        bool is_online(AxisID axis_id) override;
        void process(void) override;
        void process_isotp(void);
        /* Axis related */
        bool setup(AxisID axis_id, uint16_t baud_rate, int8_t tx_pin, int8_t rx_pin, OnGatewayPayload on_gateway_payload, OnFFBAction on_ffb_update,
                   OnAxisPayload on_axis_payload, OnAxisStateChange on_axis_state_change, OnGatewayStateChange on_gateway_state_change,
                   OnDdsSync on_dds_sync = nullptr);
        bool send_force_and_position(float &f_contact_point, float &x_contact_point) override;
        bool send_message_to_gateway(const Message &message, const uint8_t *raw_data, size_t len_raw_data) override;
        bool ready_to_receive_log_message(void) override;
        bool update_position_limits(float x_contact_point_min, float x_contact_point_max) override;
        bool update_function_id(FunctionID function_id) override;
        bool update_force(float &f_contact_point) override {
            if (own_axis_index < 0) return false;
            axis_states[own_axis_index].force_and_position.f_contact_point = f_contact_point;
            axis_states[own_axis_index].online = true;
            return true;
        }
        bool is_gateway_online(void) override {
            return _gateway_online;
        };
        /* Gateway related */
        bool send_message_to_axis(AxisID axis_id, const Message &message, const uint8_t *raw_data, size_t len_raw_data) override;
        bool send_dds_sync(float dds1_hz, float dds1_phase,
                           float dds2_hz, float dds2_phase) override;
        void set_gateway_mode(bool enable) override {
            _is_gateway = enable;
        }

    protected:
        static void task_func(void *pv_parameters) {
            CANManager *manager = (CANManager *)pv_parameters;
            delay(1000);
            for (;;) {
                manager->process();
                delay(1);
            }
        }
        void shared_setup(uint16_t baud_rate, int8_t tx_pin, int8_t rx_pin);
        bool check_bus(uint32_t ti_now);
        void update_timeouts(uint32_t now);
        bool try_process_high_prio_axis_frame(CanFrame &rx_frame, uint32_t now);
        void on_axis_seen(uint8_t axis_idx, uint32_t now, bool from_high_prio_frame = false);
        void on_axis_seen(uint8_t axis_idx) {
            on_axis_seen(axis_idx, micros());
        }
        bool try_process_low_prio_axis_frame(CanFrame &rx_frame, uint32_t now);
        void switch_bus_state(uint32_t ti_now, BusState new_state) {
            ti_state_change = ti_now;
            bus_state = new_state;
        }
        void switch_bus_state(BusState new_state) {
            switch_bus_state(micros(), new_state);
        }
        AxisState axis_states[MessageTools::MAX_AXES_COUNT] = {};
        uint8_t isotp_rx_buff[ISOTP_BUFFER_SIZE];
        uint32_t isotp_rx_size;
        uint32_t tx_err_cnt = 0;
        uint32_t rx_err_cnt = 0;
        uint32_t ti_state_change = 0;
        BusState bus_state = BusState::PRE_ONLINE;
        // State for the TEC-climb diagnostic in check_bus().
        uint32_t _tec_last_logged = 0;
        uint32_t _ti_last_tec_log = 0;
        uint32_t ti_last_ping = 0;
        uint32_t ti_last_log_sent = 0;
        /* Axis related */
        bool try_process_gateway_isotp_can_frame(CanFrame &rx_frame);
        bool try_process_ffb_update_frame(CanFrame &rx_frame);
        bool try_process_dds_sync_frame(CanFrame &rx_frame, uint32_t now);
        bool try_process_ping_frame(CanFrame &rx_frame, uint32_t now);
        // Mark gateway as online and update last-seen timestamp.
        // Called from both 0x7FE (legacy ping) and 0x0F0 (DDS sync) receivers.
        // The DDS sync path is a forward-compat fallback so 0x7FE can be retired
        // in a future firmware version without a coordinated upgrade.
        void mark_gateway_alive(uint32_t now);
        bool send_payload_to_gateway(const uint8_t *data, uint32_t len);
        void broadcast_state_updates(void);
        void broadcast_state_updates(uint32_t now);
        AxisID own_axis_id = AxisID_AXIS_UNDEFINED;
        int8_t own_axis_index = -1;
        IsotpState gateway_isotp_state;
        IsotpStateOutboundLogging outbound_logging_isotp_state;
        OnGatewayPayload on_gateway_payload = nullptr;
        OnFFBAction on_ffb_action = nullptr;
        OnDdsSync on_dds_sync = nullptr;
        float _x_contact_point_min = 0.0f;
        float _x_contact_point_max = 0.0f;
        FunctionID _function_id = FunctionID_FUNCTION_ID_UNDEFINED;
        bool _gateway_online = false;
        FlightFfbCache flight_ffb_cache[MessageTools::MAX_AXES_COUNT] = {};
        /* Gateway related */
        bool _is_gateway = false;
        bool _last_log_ack_received = true;
        void send_ping_frame(uint32_t now);
        bool try_process_axis_isotp_can_frame(CanFrame &rx_frame);
        bool send_payload_to_axis(AxisID axis_id, const uint8_t *data, uint32_t len);
        bool send_abs_trigger(const FFBAction &action);
        bool send_flight_ffb(const FFBAction &action);
        IsotpState isotp_state[MessageTools::MAX_AXES_COUNT];
        IsotpStateInboundLogging inbound_logging_isotp_states[MessageTools::MAX_AXES_COUNT];
        OnAxisPayload on_axis_payload = nullptr;
        OnAxisStateChange on_axis_state_change = nullptr;
        OnGatewayStateChange on_gateway_state_change = nullptr;
};
