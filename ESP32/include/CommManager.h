#pragma once
#include <Arduino.h>
#include <MessageTools.h>

#include "CANManager.h"
#include "CommManager.fwd.h"
#include "ConfigManager.fwd.h"
#include "GripReader.h"
#include "ICommChannel.h"
#include "MasterDds.h"
#include "SerialManager.h"
#include "ESP32OTAPull.h"

class CommManager {
    public:
        static constexpr uint8_t JOYSTICK_BUTTON_COUNT = 48;
        struct CANConfig {
            uint16_t baud_rate;
            int8_t tx_pin;
            int8_t rx_pin;
        };

        typedef std::function<void(const FFBAction &ffb_action)> OnFFBAction;
        typedef std::function<void(const AxisAction &axis_action, CommChannel comm_channel)> OnAxisAction;
        typedef std::function<void(bool ota_active)> OnOtaStateChange;
        typedef std::function<void(uint8_t dds_index, float phase, float hz)> OnDdsSync;
        void setup(Stream *serial, CANConfig &can_config, ConfigManager *config_manager, OnFFBAction on_ffb_action, OnAxisAction on_axis_action,
                   OnDdsSync on_dds_sync = nullptr, GripReader *grip_reader = nullptr);
        void set_ota_state_callback(OnOtaStateChange on_ota_state_change) {
            _on_ota_state_change = on_ota_state_change;
        }
        AxisID get_axis_id(void);
        GatewayID get_gateway_id(void);
        void process(void);
        bool send_force_and_position(float &f_contact_point, float &x_contact_point);
        bool send_message_to_gateway(const Message &message, CommChannel comm_channel);
        bool send_message_to_host(const Message &message);
        void send_active_function_message(CommChannel comm_channel);
        void send_axis_config(CommChannel comm_channel);
        void send_function_config(CommChannel comm_channel);
        bool update_position_limits(float x_contact_point_min, float x_contact_point_max);
        bool update_function_id(FunctionID function_id);
        bool get_force(AxisID axis_id, float &f_contact_point);
        bool update_force(float &f_contact_point);
        bool get_position(AxisID axis_id, float &x_contact_point);
        bool get_position_limits(AxisID axis_id, float &x_contact_point_min, float &x_contact_point_max);
        bool get_function_id(AxisID axis_id, FunctionID &function_id);
        bool is_online(AxisID axis_id);
        bool send_message_to_axis(AxisID axis_id, const Message &message);
        bool calc_controller_output_value(FunctionBase &function_base, float &controller_output);
        bool is_gateway(void) {
            return active_downlink_channel != nullptr;
        }
        bool has_gateway(void) {
            return active_uplink_channel != nullptr;
        }
        void on_physics_task_start(void) {
            _physics_task_started = true;
        }
        bool calc_input_force_sum(const AxisID *linked_axes, float &input_force);
        bool calc_input_force_sum(float &input_force);
        bool calc_input_force_sum(float own_force, float &input_force) {
            update_force(own_force);
            return calc_input_force_sum(input_force);
        }
        bool calc_final_position(float own_position, float &final_position);
        float get_controller_output_value(ControllerAxis controller_axis) {
            return controller_axis_values[MessageTools::controller_axis_index_from_id(controller_axis)];
        }
        void set_controller_output_value(ControllerAxis controller_axis, float &value) {
            controller_axis_values[MessageTools::controller_axis_index_from_id(controller_axis)] = value;
        }
        bool set_controller_button_value(uint8_t button_index, bool pressed) {
            if (button_index >= JOYSTICK_BUTTON_COUNT) return false;
            controller_button_values[button_index] = pressed ? 1 : 0;
            return true;
        }

    private:
        enum JoystickState {
            JOYSTICK_PRE_INIT,
            JOYSTICK_USB_UP,
            JOYSTICK_PRE_READY,
            JOYSTICK_READY
        };
        enum OtaState {
            OTA_IDLE,
            OTA_PREPARE_WIFI,
            OTA_WAIT_FOR_WIFI,
            OTA_CHECK,
            OTA_UPDATE,
            OTA_ERROR
        };
        bool setup_can(CANConfig &can_config);
        bool setup_serial(Stream *serial);
        void on_gateway_packet_received(const uint8_t *buffer, size_t size, CommChannel comm_channel);
        void on_gateway_message(const Message &msg, const uint8_t *protobuf_msg, uint16_t len_protobuf_msg, CommChannel comm_channel);
        void on_axis_packet_received(AxisID axis_id, const uint8_t *data, size_t len, CommChannel comm_channel);
        void on_axis_message(AxisID axis_id, const Message &msg, const uint8_t *protobuf_msg, uint16_t len_protobuf_msg, CommChannel comm_channel);
        bool send_message_to_axis(AxisID axis_id, const Message &message, const uint8_t *raw_data, uint32_t len_raw_data);
        void send_active_function_message(AxisID axis_id, FunctionID function_id, CommChannel comm_channel);
        void send_axis_log_msg(const char *buff, bool local_only = false);
        void send_gateway_log_msg(const char *buff);
        void pump_log(int max_samples, int timeout = 0);
        void on_ffb_action(const FFBAction &ffb_action);
        void periodic_task_func(void);
        void send_gateway_state_message(GatewayID gateway_id, uint8_t online_flags);
        bool send_axis_state_message(AxisID axis_id, uint8_t &online_flags);
        bool send_device_info(CommChannel comm_channel);
        void build_device_info_message(Message &msg);
        void send_joystick_values(void);
        void set_controller_axis(ControllerAxis controller_axis, float &value);
        void setup_joystick(void);
        void update_joystick_state();
        void refresh_force_pos_rate(const AxisID *linked_axes);
        void switch_joystick_state(CommManager::JoystickState new_state) {
            _joystick_state = new_state;
            _ti_joystick_state = micros();
        }
        void update_ota_state();
        void switch_ota_state(CommManager::OtaState new_state) {
            _ota_state = new_state;
            _ti_ota_state = micros();
            if (_on_ota_state_change) {
                _on_ota_state_change(new_state != OtaState::OTA_IDLE);
            }
        }
        void on_axis_state_change(AxisID axis_id, bool is_online);
        void on_gateway_state_change(ICommChannel *comm_channel, bool is_online);
        static void periodic_task(void *pv_parameters) {
            CommManager *log_output = (CommManager *)pv_parameters;
            for (;;) {
                log_output->periodic_task_func();
                delay(1);
            }
        }

        SerialManager serial_manager;
        CANManager can_manager;
        MasterDds _master_dds;
        uint32_t _ti_last_dds_sync = 0;
        ConfigManager *_config_manager;
        float _f_contact_point_own = 0.0f;
        float _x_contact_point_own = 0.0f;
        uint32_t ti_last_state_updates = 0;
        uint32_t ti_last_joystick_update = 0;
        OnFFBAction _on_ffb_action;
        OnAxisAction _on_axis_action;
        OnDdsSync _on_dds_sync;
        OnOtaStateChange _on_ota_state_change;
        Message log_msg = Message_init_zero;
        Message _state_message = Message_init_default;
        ICommChannel *active_uplink_channel = nullptr;
        ICommChannel *active_intercom_channel = nullptr;
        ICommChannel *active_downlink_channel = nullptr;
        bool _is_axis = false;
        bool _is_gateway = false;
        bool _config_manager_initialized = false;
        CANConfig _can_config;
        char _usb_product_name[30] = {};
        static const uint16_t JOYSTICK_MIN = 0;
        static const uint16_t JOYSTICK_MAX = 65535;
        JoystickState _joystick_state = JOYSTICK_PRE_INIT;
        uint32_t _ti_joystick_state;
        bool _physics_task_started = false;
        bool _force_pos_full_rate = false;
        uint8_t _force_pos_tick = 0;
        float controller_axis_values[_ControllerAxis_MAX] = {};
        uint8_t controller_button_values[JOYSTICK_BUTTON_COUNT] = {};
        GripReader *_grip_reader = nullptr;
        ESP32OTAPull ota = {};
        OtaState _ota_state = OtaState::OTA_IDLE;
        uint32_t _ti_ota_state;
        String _ota_url;
        WifiInfo _wifi_info;
        bool _device_info_sent = false;
        uint32_t _axis_ota_quiet_until = 0;
};
