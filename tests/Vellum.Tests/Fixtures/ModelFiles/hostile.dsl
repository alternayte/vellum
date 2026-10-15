workspace "Shop" "A workspace that tries to reach outside its file" {

    !include https://example.com/evil.dsl
    !include ../secrets/other.dsl
    !constant HOME_DIR "/root"

    model {
        !script groovy {
            new File("/tmp/pwned").text = "ran"
            workspace.model.addPerson("Injected", "")
        }
        customer = person "Customer" "Lives in ${HOME}"
        shop = softwareSystem "Shop" {
            api = container "Orders API" "Takes orders" "Go" {
                url https://example.com/orders
            }
            db = container "Orders DB" "Keeps orders" "PostgreSQL" "Database"
        }
        group "Payments team" {
            billing = softwareSystem "Billing"
        }

        customer -> api "places orders" "HTTPS"
        api -> db "reads and writes" "SQL"
        api -> billing "charges" "HTTPS" "Behavioural"
        api -> ghost "calls a thing defined elsewhere"
    }

    views {
        systemLandscape {
            include *
        }
        !plugin com.example.EvilPlugin
    }
}
