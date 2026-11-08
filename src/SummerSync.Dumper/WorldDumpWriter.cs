using System;
using System.Reflection;
using SummerSync.Core.Dump;
using SummerSync.Core.Json;
using UnityEngine;

namespace SummerSync.Dumper
{
    /// <summary>
    /// Writes the non-FSM parts of a dump: ArrayMaker collections, rigidbodies and vehicles.
    /// </summary>
    internal sealed class WorldDumpWriter
    {
        private static readonly FieldInfo[] DrivetrainFields = PrimitiveFieldsOf(typeof(Drivetrain));

        private readonly PathCache paths;
        private readonly GameValueWriter values;

        public WorldDumpWriter(PathCache paths, GameValueWriter values)
        {
            this.paths = paths;
            this.values = values;
        }

        /// <summary>
        /// ArrayMaker keeps game state in ArrayList and Hashtable components next to the FSMs; it does not show
        /// up as FSM variables.
        /// </summary>
        public void WriteCollection(JsonWriter writer, PlayMakerCollectionProxy proxy)
        {
            var arrayList = proxy as PlayMakerArrayListProxy;
            var hashTable = proxy as PlayMakerHashTableProxy;

            // The collection object only exists once the proxy has awoken; until then items is null.
            object items = arrayList != null ? (object)arrayList._arrayList : hashTable != null ? hashTable._hashTable : null;
            string type = arrayList != null ? "ArrayList" : hashTable != null ? "Hashtable" : proxy.GetType().Name;

            writer.BeginObject();
            WriteIdentity(writer, proxy);
            writer.Property(DumpFormat.CollectionType, type);
            writer.Property(DumpFormat.Reference, proxy.referenceName ?? string.Empty);
            writer.Name(DumpFormat.Items);
            values.CollectionItems.Write(writer, items);
            writer.EndObject();
        }

        public void WriteRigidbody(JsonWriter writer, Rigidbody body)
        {
            writer.BeginObject();
            WriteIdentity(writer, body);
            WriteBodyState(writer, body);
            writer.Property(DumpFormat.Layer, body.gameObject.layer);
            writer.EndObject();
        }

        /// <summary>A vehicle is any GameObject with UnityCar's CarDynamics component.</summary>
        public void WriteVehicle(JsonWriter writer, CarDynamics car, bool full)
        {
            GameObject gameObject = car.gameObject;
            Transform root = car.transform;

            writer.BeginObject();
            WriteIdentity(writer, car);

            writer.Name(DumpFormat.Components).BeginArray();
            Component[] components = gameObject.GetComponents<Component>();
            for (int i = 0; i < components.Length; i++)
            {
                // A missing script shows up as a null entry.
                if (components[i] != null)
                {
                    writer.Value(components[i].GetType().Name);
                }
            }

            writer.EndArray();

            writer.Name(DumpFormat.Body);
            var body = gameObject.GetComponent<Rigidbody>();
            if (body == null)
            {
                writer.Null();
            }
            else
            {
                writer.BeginObject();
                WriteBodyState(writer, body);
                writer.EndObject();
            }

            writer.Name(DumpFormat.Drivetrain);
            var drivetrain = gameObject.GetComponent<Drivetrain>();
            if (drivetrain == null)
            {
                writer.Null();
            }
            else
            {
                writer.BeginObject();
                for (int i = 0; i < DrivetrainFields.Length; i++)
                {
                    writer.Name(DrivetrainFields[i].Name);
                    values.Parameters.Write(writer, DrivetrainFields[i].GetValue(drivetrain));
                }

                writer.EndObject();
            }

            writer.Property(DumpFormat.Wheels, gameObject.GetComponentsInChildren<Wheel>(true).Length);

            if (full)
            {
                // Collider shapes relative to the vehicle root: the raw material for the passenger proxy cabins.
                writer.Name(DumpFormat.Colliders).BeginArray();
                Collider[] colliders = gameObject.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                {
                    if (colliders[i] != null)
                    {
                        WriteCollider(writer, root, colliders[i]);
                    }
                }

                writer.EndArray();
            }

            writer.EndObject();
        }

        private static FieldInfo[] PrimitiveFieldsOf(Type type)
        {
            // Only plain numbers, flags and enums: object references would pull half the vehicle in.
            return Array.FindAll(
                type.GetFields(BindingFlags.Public | BindingFlags.Instance),
                field => field.FieldType.IsPrimitive || field.FieldType.IsEnum);
        }

        private void WriteIdentity(JsonWriter writer, Component component)
        {
            writer.Property(DumpFormat.Path, paths.NamePath(component.transform));
            writer.Property(DumpFormat.IndexPath, paths.IndexPath(component.transform));
            writer.Property(DumpFormat.InstanceId, component.GetInstanceID());
            writer.Property(DumpFormat.ActiveInHierarchy, component.gameObject.activeInHierarchy);
        }

        private static void WriteBodyState(JsonWriter writer, Rigidbody body)
        {
            writer.Name(DumpFormat.Position);
            GameValueWriter.WriteVector(writer, body.position);
            writer.Name(DumpFormat.Rotation);
            GameValueWriter.WriteQuaternion(writer, body.rotation);
            writer.Name(DumpFormat.Velocity);
            GameValueWriter.WriteVector(writer, body.velocity);
            writer.Name(DumpFormat.AngularVelocity);
            GameValueWriter.WriteVector(writer, body.angularVelocity);
            writer.Property(DumpFormat.Mass, body.mass);
            writer.Property(DumpFormat.Kinematic, body.isKinematic);
            writer.Property(DumpFormat.UseGravity, body.useGravity);
            writer.Property(DumpFormat.Sleeping, body.IsSleeping());
        }

        private void WriteCollider(JsonWriter writer, Transform root, Collider collider)
        {
            Transform transform = collider.transform;

            writer.BeginObject();
            writer.Property(DumpFormat.Path, paths.RelativePath(root, transform));
            writer.Property(DumpFormat.ColliderType, collider.GetType().Name);
            writer.Property(DumpFormat.IsTrigger, collider.isTrigger);
            writer.Property(DumpFormat.Enabled, collider.enabled);
            writer.Property(DumpFormat.ActiveInHierarchy, collider.gameObject.activeInHierarchy);
            writer.Property(DumpFormat.Layer, collider.gameObject.layer);

            // Pose of the collider's transform in the vehicle's local space.
            writer.Name(DumpFormat.Position);
            GameValueWriter.WriteVector(writer, root.InverseTransformPoint(transform.position));
            writer.Name(DumpFormat.Rotation);
            GameValueWriter.WriteQuaternion(writer, Quaternion.Inverse(root.rotation) * transform.rotation);
            writer.Name("scale");
            GameValueWriter.WriteVector(writer, transform.lossyScale);

            // Shape in the collider's own local space.
            var box = collider as BoxCollider;
            var sphere = collider as SphereCollider;
            var capsule = collider as CapsuleCollider;
            var mesh = collider as MeshCollider;
            if (box != null)
            {
                writer.Name(DumpFormat.Center);
                GameValueWriter.WriteVector(writer, box.center);
                writer.Name(DumpFormat.Size);
                GameValueWriter.WriteVector(writer, box.size);
            }
            else if (sphere != null)
            {
                writer.Name(DumpFormat.Center);
                GameValueWriter.WriteVector(writer, sphere.center);
                writer.Property("radius", sphere.radius);
            }
            else if (capsule != null)
            {
                writer.Name(DumpFormat.Center);
                GameValueWriter.WriteVector(writer, capsule.center);
                writer.Property("radius", capsule.radius);
                writer.Property("height", capsule.height);
                writer.Property("direction", capsule.direction);
            }
            else if (mesh != null)
            {
                writer.Property("convex", mesh.convex);
                Mesh shared = mesh.sharedMesh;
                if (shared != null)
                {
                    writer.Property("mesh", shared.name);
                    writer.Name(DumpFormat.Center);
                    GameValueWriter.WriteVector(writer, shared.bounds.center);
                    writer.Name(DumpFormat.Size);
                    GameValueWriter.WriteVector(writer, shared.bounds.size);
                }
            }

            writer.EndObject();
        }
    }
}
