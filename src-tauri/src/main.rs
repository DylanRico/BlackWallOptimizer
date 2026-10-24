#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod hardware;
mod optimizer;

use optimizer::CommandExecutionResult;

#[tauri::command]
async fn scan_hardware() -> Result<serde_json::Value, String> {
    hardware::collect_deep_hardware_audit().await
}

#[tauri::command]
async fn get_catalogs() -> Result<serde_json::Value, String> {
    let tweaks_raw = include_str!("../../catalogs/tweaks.json");
    let apps_raw = include_str!("../../catalogs/apps.json");
    let version_raw = include_str!("../../catalogs/version.json");

    let tweaks: serde_json::Value = serde_json::from_str(tweaks_raw).map_err(|e| e.to_string())?;
    let apps: serde_json::Value = serde_json::from_str(apps_raw).map_err(|e| e.to_string())?;
    let version: serde_json::Value = serde_json::from_str(version_raw).map_err(|e| e.to_string())?;

    Ok(serde_json::json!({
        "tweaks": tweaks["tweaks"],
        "apps": apps["apps"],
        "categories": apps["categories"],
        "version": version
    }))
}

#[tauri::command]
async fn check_all_tweaks() -> Result<serde_json::Value, String> {
    optimizer::check_tweaks_state().await
}

#[tauri::command]
async fn check_installed_apps() -> Result<serde_json::Value, String> {
    optimizer::check_installed_apps().await
}

#[tauri::command]
async fn get_restore_points() -> Result<serde_json::Value, String> {
    optimizer::get_system_restore_points().await
}

#[tauri::command]
async fn execute_tweak_commands(commands: Vec<String>) -> Result<CommandExecutionResult, String> {
    optimizer::run_ps_commands(commands).await
}

#[tauri::command]
async fn create_restore_point(description: String) -> Result<CommandExecutionResult, String> {
    optimizer::create_system_restore_point(&description).await
}

#[tauri::command]
async fn get_live_scripts() -> Result<serde_json::Value, String> {
    optimizer::list_active_system_scripts().await
}

#[tauri::command]
async fn kill_script_process(pid: u32) -> Result<CommandExecutionResult, String> {
    optimizer::kill_process_tree(pid).await
}

#[tauri::command]
async fn install_app(
    winget_id: String,
    fallback_url: String,
    silent_args: String,
) -> Result<CommandExecutionResult, String> {
    optimizer::install_app_unattended(&winget_id, &fallback_url, &silent_args).await
}

#[tauri::command]
async fn perform_ota_update(
    download_url: String,
    expected_sha256: String,
) -> Result<CommandExecutionResult, String> {
    optimizer::perform_ota_self_update(&download_url, &expected_sha256).await
}

fn cleanup_previous_ota_binary() {
    if let Ok(current_exe) = std::env::current_exe() {
        let old_exe = current_exe.with_extension("exe.old");
        if old_exe.exists() {
            let _ = std::fs::remove_file(old_exe);
        }
    }
}

fn main() {
    cleanup_previous_ota_binary();

    tauri::Builder::default()
        .invoke_handler(tauri::generate_handler![
            scan_hardware,
            get_catalogs,
            check_all_tweaks,
            check_installed_apps,
            get_restore_points,
            execute_tweak_commands,
            create_restore_point,
            get_live_scripts,
            kill_script_process,
            install_app,
            perform_ota_update
        ])
        .run(tauri::generate_context!())
        .expect("Error al iniciar la aplicación Blackwall");
}
